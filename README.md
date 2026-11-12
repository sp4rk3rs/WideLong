<p align="center">
  <img src="app/WideLong/Assets/WideLongLogo.png" alt="WideLong logo" width="180">
</p>

# WideLong

Combine CSV files, reshape between wide and long, clean values, and export the dataset with readable R or SAS code that rebuilds it from the original files.

*Windows prototype · version 0.6.0 · by Parker McKasson · [MIT License](LICENSE)*

A visual, offline CSV preparation workspace. Select variables by source file, match records by ID with optional additional and time/grouping keys, reshape repeated columns, correct values, and export a smaller dataset for R or SAS.

Everything runs locally: no network calls, telemetry or automatic saving. See [PRIVACY.md](PRIVACY.md).

> **AI assistance.** This project was developed with substantial help from AI coding assistants: Claude Opus 5.5, Sol 6.1, and Astra 6. They drafted and revised much of the code, tests and documentation at the author's direction. Every release is built and tested, and the generated R code is checked by running it in R against the application's own output. See [Acknowledgments](#acknowledgments).

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) with Windows Desktop support, to build (the .NET 10 Desktop Runtime is enough to run a build)
- Optional: R 4.x, to run the tests that execute generated R code

## Start

Build first (see [Build and verify](#build-and-verify)). Then double-click **Start WideLong.cmd**, or run **output/WideLong/WideLong.exe**. Keep the executable and its neighboring runtime files together. This build uses the **.NET 10 Desktop Runtime**. `./Build.ps1 -Publish` also makes `output/WideLong-<version>-win-x64.zip`: a single self-contained `WideLong.exe` that runs without installing .NET, suitable for a GitHub release. It does not download a runtime, packages, or application assets during use.

Open **Examples…** and choose a structure resembling your files. Every example is fictional; no data loads automatically.

| Example | Files and structure | Useful first action |
| --- | --- | --- |
| Health study | 600 participants in five files: demographics and enrollment (one row per person), visits and lab results (long), and 42 wide health-measurement columns | Switch between ID and ID + visit |
| School | 120 students in five files: student details, wide grades, and long attendance, behavior and reading logs per term | Match grades to attendance by term |
| Shop | 80 customers in five files: customers, orders, returns, support tickets and loyalty details | Sort by region and order total |
| Monthly reports | Five matching department-budget files (January–May) | Stack rows while retaining source labels |
| Messy survey | 60 respondents in five files; leading-zero IDs, missing-value codes, yes/no, repeated scores and follow-up calls | Accept the numbers prompt, set missing-value codes, and export cleaning code |
| Household | Four households in five files; ID-only details and contacts, person details, long questionnaires and role-specific wide scores; Mother/Father/Child share IDs | Match ID + person + time; right-click person → Filter responses… to leave out Child |

## Workflow

1. **Import CSV**: select one or more files, delimiter, encoding, missing-value rules and where the file comes from. **Detect automatically** recognizes Qualtrics exports and skips their two description rows (question text and import IDs) under the header; you can also choose Qualtrics, REDCap or a plain CSV explicitly. REDCap's `record_id` and `redcap_event_name` are suggested as ID and time. Files saved from Excel that are not UTF-8 get a suggestion to choose Windows-1252. Headers must be unique and nonempty. Supported delimiters are comma, semicolon, tab, and pipe; encodings are strict UTF-8, UTF-16 LE, and Windows-1252. Quoting, embedded commas/newlines, Unicode, and leading zeros are preserved. Blank physical lines are skipped. Imported variables start as Text; WideLong then lists variables that are entirely or almost entirely numbers (at least 80% of non-missing values, up to five non-number codes such as "not given") and asks once whether to treat them as numbers. **Yes** converts them as recorded cleaning steps, turning the listed codes into missing values; **No** keeps everything as text. Values with leading zeros (IDs, ZIP codes) are never offered. Right-click a variable → **Revert variable type** returns it to its original text at any time.
2. **ID / grouping** on a file: review its ID, optional **Additional matching column**, and optional time/grouping column. The additional column can distinguish people or roles sharing an ID; time/grouping distinguishes their repeated records. Recommendations run locally using column names, completeness, uniqueness, and shared ID values across files. **Review matching…** shows missing/duplicate keys and exact cross-file ID coverage before combining files. The full set of configured keys must be unique within each file. Your explicit choices take precedence over later recommendations. Matching is exact and case-sensitive; numeric uniqueness alone does not establish an ID.
3. Use the **left sidebar** to search variables, expand/collapse source files, and select what remains. Required keys are marked. Click a variable name to find it in the grid. Pause over its name for an action menu, or right-click immediately.
4. **Match IDs and groups** combines files by ID and exact additional/time values when configured. ID-only values apply to every person and time for that ID; a file with ID + time but no additional key applies its values to each person at that matching time. Person-specific values stay with that person. Keep all IDs, IDs present in every file, or IDs from the first file. **Stack matching columns** aligns variables by name, keeps records from different files separate, and adds source-file provenance.
5. The layout order is **ID**, **ID + the selected matching column names**, **Custom variables**, **Source rows**. In the wide layout, time columns follow the order in which time values occur within each ID (an ID without Baseline no longer pushes Baseline after Month 3); right-click a time column → **Order time values…** to set the order yourself. **ID** is wide: one row per ID, with person/time observations in separate columns when those keys are configured. The long layout keeps one row per complete configured key, such as **ID + person + time**. **Custom variables** puts only explicitly chosen variables into `variable` / `value` rows; other variables remain context columns. **Source rows** / **View file** shows a chosen source file. The active layout is dark purple, and a **Viewing: Wide / Long / Custom / Source rows** badge identifies the displayed shape. Switching layouts does not alter source rows.
6. **Click a column header** to add it as the next sorting variable. Its header becomes dark purple, cells are shaded, and a number shows priority. Click again to remove it. Sorted variables move to the far left in priority order. A column's right-click menu shows **Add to sorting** or **Remove from sorting** as appropriate; unavailable actions are greyed and explain why when hovered. The **Sort by** chips show the order; arrows reverse direction and × removes a rule. Drag chips to change priority.
   **Search variables to sort by…** (in the Sort by bar) finds any output variable by name or source file, which is faster than scrolling across hundreds of columns. Arrow keys move through matches; Enter adds the highlighted variable ascending, Shift+Enter descending. Variables already in the sort order are not listed.
7. **Drag any column header** to move it. Moving a sorted column also sets active sorting priorities from left to right. Moving an unsorted column preserves sorting priority. Each layout retains its own column order, sorting, and output names during the session. Hidden variables retain their preferences. Numbers sort numerically, Text sorts as text, missing values sort last, and ties retain import order.
8. **Hover for about one second or right-click a header** for Rename, Change type, Replace values, missing-value rules, sorting, and repeated-column settings. **Map columns…** opens a staged batch mapping table. Hover menus do not take keyboard focus; they dismiss when leaving, dragging, editing, pressing Escape, or switching away. Renaming an output affects that layout; renaming a source variable affects the working source across layouts. Generated or mixed-value columns may require editing the named source variable instead.
9. Double-click an existing source value to correct it. The correction appears wherever that source cell is used. Combined key cells and cells absent from a source require editing in **Source rows**. Ctrl+Z / Ctrl+Y undo/redo the last 12 data changes. Sorting and column order are separate presentation preferences. Originals remain untouched.
10. The preview draws one page of rows at a time (100 by default; choose 50–1,000 under **Rows shown**). Use « ‹ Previous / Next › » or Ctrl+Page Up / Ctrl+Page Down to move between pages. Sorting, search, filter rules, counts and every export always use all rows; paging only limits what the grid draws. Recently viewed layouts stay in memory, so switching back to a layout or file you just viewed is immediate until the data changes.
11. **Search rows** finds literal text across output columns, ignoring case. **Filter rules…** adds typed comparisons combined with AND or OR. Search and filter rules both apply to the preview and export; missing cells match only explicit missing/not-missing rules. Variable search affects only the sidebar.
12. **History** shows data changes, before/after examples, and separate view/filter activities for this session. Undo/Redo covers the latest 12 data changes; view preferences are changed through their own controls.
13. **Export data…** chooses the shape, destination software, and CSV or ZIP packaging, then opens an export review. Inspect the actual output before choosing a destination. Only the final explicit Save writes your dataset.

The fixed palette uses primary **#50179a** and secondary **#ada1bd**. There is no color-change setting. Changes are recorded in [CHANGELOG.md](CHANGELOG.md).

## Review file matching

**Review matching…** remains available when a key problem prevents the combined preview from being built. It shows each file's selected ID/additional/time columns, source row count, distinct complete IDs, distinct complete keys, missing-key rows, duplicate keys, and all records belonging to duplicate keys. The selected file's detail text separates missing IDs from other missing selected keys and reports excess duplicate records.

The coverage table compares exact, case-sensitive ID values with every other configured file. Percentages use the selected file's distinct IDs from complete key records as their denominator. Records with a missing selected key do not contribute to coverage; duplicate keys are reported separately. This is ID coverage, not a claim that every person/time combination matches across files.

Inspect all problems, missing keys, duplicate keys, or unmatched IDs; narrow unmatched issues to another file or search ID/grouping text. Counts cover all matching issues; at most 500 are displayed until the selection is narrowed. Source record numbers exclude the CSV header. **Go to selected source record** clears the current search/filter rules, opens Source rows, and locates the record by source provenance even when the grid has been sorted. **Edit this file's keys…** opens its key settings. Reopen the review after edits to recalculate diagnostics. The review never deletes records or repairs keys automatically.

### Matching people or roles within one ID

The household example demonstrates why an additional key can be necessary. Mother, Father and Child may share both `household_id` and `time`; those are different records when `person` differs.

| File structure | ID | Additional matching column | Time/grouping |
|---|---|---|---|
| One row per household | `household_id` | None | None |
| Long questionnaire rows | `household_id` | `person` | `time` |
| Wide person scores with visit labels in column names | `household_id` | `person` | None; mapped columns supply the visit labels |

With these settings, matching uses ID + person + time. Household-level details are shared with every role and time. A roleless ID + time file applies only at its matching time across roles. Role-specific values never transfer to another role, and another role's follow-up does not create a missing person's follow-up row. The household example includes uneven Child follow-up schedules and genuine missing scores to illustrate that distinction.

Long output keeps the `person` key in its own column. Wide output stays at one row per household ID and uses names such as `confidence__Mother__Baseline`; ID-only values stay in a single unsuffixed column. Role-qualified columns are created only for roles observed in that variable's source, so the household example does not invent Child confidence/focus columns. A selected source cell whose value is genuinely missing is still retained. Repeated rows with the same full configured key, or missing values in any selected key, remain errors to resolve through **Review matching…**.

## Missing values and types

Import and **ID / grouping** settings provide file defaults: **Treat empty cells as missing** and an optional list of other missing-value codes, one exact code per line. Empty cells are missing by default. Literal `NA`, `NULL`, and `.` remain text unless explicitly listed. Rules apply to the working copy; original values stay available in memory for reproducible code.

Use a variable's **Missing-value rules…** menu action to inherit those file defaults or provide a variable-specific override. For example, `99` can mean missing in a survey-score variable while staying a real value in another variable. A repeated-column family can receive the same override together. Choose the file-default mode to remove an override. The dialog previews missing counts, newly missing values, and values that become visible again without rewriting their original text; rules that expose invalid values for the current type are refused.

Uncheck the empty-cell rule to retain genuine empty Text values. Numeric, Date, and Boolean blanks are always missing. The preview displays missing cells as `—`; this display marker is not written as data. SAS character values cannot distinguish a genuine empty string from character missing.

- **Text** preserves leading-zero identifiers and literal codes.
- **Number** uses decimal-point notation. Group separators such as `1,234` are not guessed. Keep long identifiers and exact high-precision values as Text for R/SAS floating-point compatibility.
- **ID, additional-key and time columns** can be converted like any other variable (right-click the combined column header or the key in the sidebar). The combined key column takes that type when every file's key column has the same type, so numeric IDs sort as numbers and export as numeric in R/SAS; if files disagree, it quietly stays Text. Converting offers to change the matching key in every file together. **Explicit key conversions can change their text:** Number removes leading zeros and normalizes numeric text (for example `00001` → `1` or `201.0` → `201`). Review the preview and keep the every-file option checked to convert matching keys together. Conversion is refused if distinct key values within or across the selected files would collapse into one (for example `007` and `7`), if values cannot convert, or if a key value matches a missing-value code. Undo and Revert variable type can restore the original padded text. Automatic number detection still leaves leading-zero IDs as Text.
- **Date** requires an explicit input format, initially `yyyy-MM-dd`, and stores ISO dates. Examples include `MM/dd/yyyy` and `dd/MM/yyyy`. Datetime/timezone conversion is outside this prototype.
- **Boolean** accepts true/false, yes/no, and 1/0, ignoring case, and stores true/false.

Type changes validate every value before applying. The preview shows representative before/after conversions, invalid values, total and newly missing values, and a count of leading-zero losses. Unrecognized values must be resolved rather than silently becoming missing. Each conversion records the effective missing-code rules for each affected variable so cleaning code can replay that operation even if policies change later. **Apply to every repeated column** converts a family together; grouped columns must have consistent types.

## Repeated columns and custom rows

Patterns such as `score_m0`, `score_m3`, `math_term1`, and `math_term2` suggest a shared variable and grouping values. Detection requires at least two related columns. Bare numeric endings such as `question_1` and `question_2` are not assumed to represent time. **Repeated column settings…** lets you change or clear one mapping. **Map columns…** opens the batch editor, also available through source-variable menus. Group labels remain literal: `Baseline`, `0`, and `m0` differ.

Choose a source file, then edit its non-key columns in the mapping table. Each row shows the source name, type, Keep selection, shared variable, and grouping value. Select adjacent rows with Shift or separate rows with Ctrl, assign a shared variable together, and either retain grouping labels, explicitly infer them from column suffixes, or supply a manual label. Individual labels can also be edited in the grid. Clear selected mappings to return those columns to ordinary columns.

Changes remain staged until **Apply mappings**. The live preview shows the first source record as original columns and grouped rows, using up to 20 kept mapped columns. Incomplete mapping pairs, duplicate shared-variable/grouping pairs, and inconsistent types within a family must be resolved before applying; validation also covers currently excluded mappings. No source values or original column names are rewritten. Cancel discards the staged edits.

Unchecking a repeated source column removes its corresponding wide output column, even if another file contains that time point. A selected column whose values are all missing remains in the output.

**Custom variables** is optional and starts without an automatic selection. **Choose variables to stack…** shows each detected repeated family separately, its source columns, and how its grouping/value rows would look. Selecting a family keeps unrelated numeric variables as context. You can also select arbitrary variables manually. Choosing weight and heart rate, for example, creates separate variable/value rows while retaining the ID, grouping, and other context columns.

Only selected source cells create custom rows. A selected missing cell is retained; an unmatched record without any selected source cells produces no value row. The shared value column uses a common type when possible and Text for mixed types. Variables remain adjacent by source file, collisions are qualified by source, and wide names use `variable__grouping_value` or `variable__role__grouping_value` when an additional matching key is configured.

## Scale scores and value labels

Right-click a variable → **New scale score…** to score a questionnaire: choose numeric items of one file, mark reverse-coded items (a 1–5 answer x becomes 1 + 5 − x), choose mean or sum, and the minimum number of answered items. The score is added as a new Number variable after the file's columns, recomputed whenever its items change, and written into the R and SAS cleaning code. Missing-value codes of the items (for example 99) are left out. Right-click the score → **Edit scale…** or **Remove scale**.

**Value labels…** attaches labels such as `1 = Strongly disagree` to a Text or Number variable. Values are unchanged. Exports list the labels in the dictionary; `import.R` makes a factor when every value is labelled (otherwise it keeps the values with a `labels` attribute, the convention haven/labelled read); `import.sas` and the SAS cleaning program create formats; the R cleaning script adds the `labels` attribute.

## Recipes

**Recipe ▾ → Save recipe…** writes a `.widelong.json` file with everything needed to rebuild this session from the original files: ID / grouping choices, kept and renamed variables, types, missing-value rules, mappings, response filters, corrections and recodes, scales, value labels, time order, layouts, sorting, column names and filter rules. It contains no data rows; values typed into corrections and recodes and the files' folder paths are included, as in the cleaning code. **Open recipe…** finds each original file (its recorded path, then the recipe's folder, otherwise it asks), checks that its columns match, and reapplies every step. It stops if a recorded single-value correction no longer finds its value, and reports files that have gained or lost rows.

## Filter responses (keep rules)

Right-click a variable (in the sidebar or a column header) → **Filter responses…** lists every response with its record count, plus *(missing)* when there are missing values. Checked responses stay; records with an unchecked response are removed from that file **before files are combined**, so every layout, export and cleaning script leaves them out. For example, uncheck *Informant* under person to drop informant records in every shape (wide exports no longer get `__Informant` columns). A filter on a matching key (ID, person, time) applies to that column in every file, including files imported later. The sidebar marks filtered variables with “· filtered”, and the menu reads **Filter responses… (active)**. Choose **Select all** and Apply to remove the filter. Repeated columns (one per time) use Filter rules instead, because one response would remove a whole record. Changing or reverting a variable's type, or **Replace values** on a filtered response, keeps the same records.

## Search and typed filter rules

**Search rows** is the existing case-insensitive text search across output columns. **Filter rules…** is a separate typed rule builder: choose a variable, an available comparison, and a value, then keep rows matching **All rules (AND)** or **Any rule (OR)**. Supported comparisons depend on type and include equality, inequality, text contains/not-contains, numeric/date comparisons, inclusive ranges, and explicit missing/not-missing tests. Dates use `yyyy-MM-dd`; Boolean values accept true/false, yes/no, and 1/0. Text comparisons are case-sensitive.

Missing cells do not satisfy ordinary comparisons, including not-equal and not-contains; use an explicit missing rule when those records should be included. The dialog reports how many rows satisfy its draft rules before Search rows is applied. Search and the applied rule group are combined: a row must satisfy both.

Rules track stable output-variable identities. If a layout, selection, or type change makes a rule unavailable or incompatible, the preview/export is blocked with a clear message. Open the rule builder to change or remove that rule, or restore the required variable; rules are never silently skipped for another export shape. The same typed predicates are included in generated R and SAS cleaning programs. Filters and search stay in memory and are applied separately to each requested export shape.

## Session history and Undo

**History** lists session actions in time order with categories and current states. Selecting a data change shows up to 50 before/after examples of affected values or settings, including variable selection, names/types, missing rules, mappings, and key changes. Data changes report how many stored cell values changed; a missing-rule update can reinterpret values without changing that stored text.

Undo/Redo covers the latest 12 data changes and the history marks their state. View, navigation, and filter activities are listed separately; sorting, output naming/order, layout, row search, and typed rules remain view preferences rather than data Undo steps. Change those through the relevant controls. History is not a saved session, recovery file, or automatic export. Clearing/closing the session discards it.

## Export data

The export dialog offers:

- **Wide**: one row per ID; repeated observations use separate columns, qualified by role and time when configured.
- **Long**: one row per complete configured key, such as ID + grouping or ID + person + time; repeated observations use separate rows.
- **Both**: a ZIP with separate `wide/` and `long/` folders.
- The current **Custom** or **Source rows** layout, when applicable.

The displayed shape is marked **Current view** and selected by default. You may export another shape without changing the preview. Each shape uses its own in-session sorting, names, and column order; a shape not yet configured uses its default order. Search and typed filter rules are applied separately to each exported shape, so wide and long row counts can differ beyond reshaping. If a rule's variable is unavailable in a requested shape, change/clear the rule or choose a compatible shape before exporting. Wide export requires matching rather than stacking independent source records.

Name the dataset first. The ZIP and every file inside it use that name (the Save dialog starts with it; changing the file name there renames everything to match). Then choose **R**, **SAS**, or **Other software**:

- R missing values use `NA`. The recommended ZIP reader creates actual typed R missing values. If literal `NA` text exists, the bundle selects a collision-free missing token and its reader preserves both. Plain R CSV export refuses that ambiguity and directs you to ZIP.
- SAS numeric, Date, and Boolean missing values use `.`; character missing uses blank. SAS cannot preserve a distinction between genuine empty Text and character missing.
- Other-software CSV uses blank missing values. CSV alone carries no type metadata and may not preserve the distinction between missing and empty text.

After these options, **Review your export** shows the actual prepared output before opening a destination picker. Each shape has its own tab with row/column counts, missing-cell counts, the software's missing-value code, included filenames, and the first eight rows using actual exported column names and codes. An expandable name list shows display names changed for SAS-compatible bundle headers. **Variable summary** lists each column's N, missing count and share, distinct values, minimum, maximum and mean, and flags columns to check (all or over half missing, one value only, numbers more than 3 interquartile ranges from the middle half). Review all tabs for Both; Cancel returns without writing anything. **Choose destination…** opens the native Save dialog, and only confirming that dialog proceeds to write the selected export.

A single-layout ZIP named `NAME.zip` contains (for R; SAS uses `.sas`):

- `NAME.csv`: the dataset. SAS and Other-software exports use unique SAS-compatible headers of at most 32 ASCII characters; R exports keep the names shown in WideLong (the same names `clean.R` produces).
- `NAME dictionary.csv`: exported name, display name, chosen type, source, and value labels when any.
- `NAME import.R` / `NAME import.sas`: reads `NAME.csv` with explicit missing-value and type handling.
- `NAME clean.R` / `NAME clean.sas` (when **Include the cleaning code** is checked): rebuilds the dataset from your original files; see below.
- `NAME README.txt`: what each file is and the missing-value conventions.

**Both** puts `NAME_wide.*` and `NAME_long.*` in `wide/` and `long/` subfolders. Only the chosen software's scripts are included; **Other software** gets the dataset, dictionary and README. If cleaning code cannot be generated (for example an unrecorded change), the review says so and the export continues without it.

Bundles currently reject embedded line breaks and SAS character-width limits before creating a ZIP; CSV-only export supports multiline values. Both CSV and ZIP are unencrypted. Imported original paths cannot be overwritten through export.

## Reproducible SAS / R cleaning code

The cleaning code reads your **original files** and rebuilds the prepared dataset step by step, written the way a person would clean the data by hand, with a comment above each group of changes. Get it from **Export data…** (included next to the dataset) or **Cleaning code…** (view, copy or save).

Both languages follow the same outline:

1. **Read the original files**, every column as text (codes and leading zeros survive), with a check of each file's row count (R).
2. **Clean each file**: renamed columns; type changes of matching keys, rewritten to the same text WideLong matches on (`00001` → `1`, `3/5/2025` → `2025-03-05`); single-value corrections (`dem[12, "age"] <- 34  # was "340"`); recodes; type changes grouped together ("Store as numbers; "-99" counts as missing"); missing-value codes; scale scores; **Filter responses** (`dem <- dem[dem$person %in% c("P", "S"), ]  # leaves out "I"`); and the selected variables.
3. **Combine the files**: repeated columns become rows where needed, then one row per ID (+ person) (+ time) in the order IDs first appear, and each file's variables are matched on the keys it has. Stacked files keep a `source_file` column.
4. **Final columns, filters and sorting**: matching keys get their final type, then column order, names chosen in WideLong, filter rules, Search rows and sorting.

**Cleaning code…** opens on R, the language whose generated code is verified by running it. R uses base R only and ends with a data frame named `prepared` (a commented `write.csv()` line saves it). SAS ends with `WORK.NAME`; set the `folder` macro variable at the top, and a commented LIBNAME block saves a permanent copy. Small helpers (`read_text`, `stack_columns`, `spread`) are defined once with a comment when the layout needs them. Corrections and recodes refer to data rows in the original file order. The program never writes output automatically.

For built-in examples, **Save example files + code ZIP…** writes unchanged fictional source CSVs, `clean.R`, `clean.sas` when supported, and instructions. Imported originals are never included by this action.

Scripts contain local paths and explicitly corrected/recoded values; review them before sharing. Full unchanged datasets are not embedded. The application does not execute generated R or SAS. SAS code currently rejects multiline/control-character text (including in skipped Qualtrics description rows), leading/trailing whitespace distinctions, oversized characters/labels, and unsupported date formats. Folder or file names containing `&`, `%`, `;`, quotes or parentheses are written as full quoted paths instead of the `folder` macro variable. Two-digit-year date patterns (`yy`) are refused everywhere, because WideLong, R and SAS assign centuries differently. Unsupported numeric precision/exponent replay is blocked; retain such values as Text or export prepared data. R programs are tested by running them in R on fictional sources and requiring exactly the application's output; SAS programs are generated by the same plan but have not been run in SAS. Release-specific R and SAS validation results and limitations are recorded in [CHANGELOG.md](CHANGELOG.md).

## Privacy and scope

**Your data stays private and remains on this computer only. This app does not send or save it** (other than files you explicitly export). Import and export are explicit each session. Closing or clearing discards the in-memory session; originals remain untouched.

The implementation has no model integration, network client, server, telemetry, updater, automatic recovery, dataset cache, or disk-spill path. Original/working values, previews, edits, source paths, histories, and layout preferences remain in process memory. Source paths appear only in explicit cleaning-code exports. See [PRIVACY.md](PRIVACY.md) for implementation boundaries.

Windows controls paging, crash dumps, file-dialog metadata, clipboard behavior, and folder synchronization. Exporting into a cloud-synced folder follows that folder's existing sync settings. Managed memory is not cryptographically zeroed. This prototype has no independent security audit or compliance certification.

There is no arbitrary row/column cap, but available RAM limits operations. The grid virtualizes visible cells; original and working sources, previews, undo snapshots, and prepared exports still use memory. Oversized operations stop rather than spilling data to disk. Matching supports ID, one optional additional matching column, and one optional time/grouping column. Arbitrary additional key columns, many-to-many joins, automatic aggregation, nested repeated structures, native SAS/R binary inputs and statistical analysis are outside current scope. Recipes save settings, not data, so a reopened session always reads the original files again. Ambiguous keys and lossy pivots are rejected.

## Build and verify

Install a .NET 10 SDK with Windows Desktop targeting support, then run in PowerShell from the repository root:

```powershell
./Build.ps1 -Test
# Also execute generated cleaning programs in an installed R:
./Build.ps1 -Test -Rscript 'C:/Program Files/R/R-4.5.2/bin/Rscript.exe'
# Also make a self-contained single-file release ZIP (downloads the .NET runtime packs from nuget.org):
./Build.ps1 -Publish
```

The version is set once, in `Directory.Build.props`; the window title, generated scripts and export READMEs read it from the built assembly. GitHub Actions (`.github/workflows/checks.yml`) builds and runs the whole suite, including native R replays, on every push and pull request.

To pressure-test with your own CSV files, keep them in a folder outside this repository and add `-StressData <folder>` (optionally `-StressOutput <folder>`; the default is `stress-output` inside the data folder). The run imports every CSV with common numeric missing codes, maps numbered repeated columns, converts numeric variables, IDs and times, and checks every combined cell against its source record across long, wide, custom-row, stacked, sorted, filtered and random file-combination layouts, plus export bundles, R/SAS code and native R replays of a file subset. Only `stress-report.txt` and the replay folders are written to the output folder; nothing is copied into this repository. Native R replays use the first six files, or the files whose names start with the comma-separated prefixes in `WIDELONG_STRESS_SUBSET`.

No NuGet packages are required; `NuGet.Config` clears external package sources (only `-Publish` downloads runtime packs). Builds go to `output/WideLong`; caches and explicit fictional test exports stay in `work`. Application source is in `app/WideLong`; `tests` links the same transformation/export/presentation/recipe code. Verification counts and runtime execution results belong in the changelog for the specific release.

Launch a fictional-only verification window with imports disabled:

```powershell
./output/WideLong/WideLong.exe --synthetic-check
# Start the same fictional-only check with the household ID/person/time example:
./output/WideLong/WideLong.exe --synthetic-check --compound-example
```

Local SAS 9.4 startup was attempted in an isolated batch smoke check, but it exited 115 before producing the requested log or test output. The cause was not established, so generated SAS has not been runtime-validated. The diagnostic notes are in [docs/sas-validation.md](docs/sas-validation.md); current release results are recorded in the changelog.

`scripts/run-checks.cmd` runs the build and all checks (with R when installed) and writes the results to `scripts/build.log`.

## Repository layout

| Path | Contents |
| --- | --- |
| `app/WideLong` | WPF application source |
| `tests` | Console test suite (links the application's data, export and code-generation sources) |
| `scripts` | `run-checks` helper that builds, runs every check and writes a log |
| `docs` | SAS validation notes |
| `Build.ps1` | Build, test and publish entry point |
| `Directory.Build.props` | The release version |
| `.github/workflows` | Continuous integration (build and tests with R) |
| `output/`, `work/` | Created by builds and tests; ignored by Git |

Datasets (`*.csv`, `*.tsv`, `*.xlsx`, SAS/R data files) are ignored by `.gitignore` so real data is not committed by accident. The built-in examples are generated in code, so no data files are needed.

## Acknowledgments

Developed with AI assistance from **Claude Opus 5.5** (Anthropic), **Sol 6.1**, and **Astra 6**. The assistants drafted and revised code, tests and documentation at the author's direction. Release-by-release details are in [CHANGELOG.md](CHANGELOG.md).

## License

Released under the [MIT License](LICENSE). Copyright (c) 2026 Parker McKasson.
