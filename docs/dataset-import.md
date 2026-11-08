# Dataset import

Open a writable project, select **Import dataset**, and choose a CSV, TSV, JSON array or JSONL file. Select encoding, delimiter and conversion culture (blank means invariant). UTF-8, UTF-16 LE and Windows-1252 are available. JSON nesting is preserved; flattening is not offered yet.

Mappings are an explicit JSON array, for example:

```json
[{"Source":"id","Target":"_caseId"},{"Source":"amount","Target":"price","Type":"Decimal"}]
```

An empty mapping array preserves all fields. Supported types are Preserve, String, Decimal, Boolean and Date (saved as ISO date). `EmptyStringAsNull` is opt-in. Missing JSON properties stay missing; explicit nulls stay null. CSV values retain leading zeros unless explicitly converted. When selecting mappings, include `_caseId` if it already exists.

**Prepare preview** reads and validates the complete source, applies conversions, checks unique nonempty string case IDs and writes a temporary spool. Missing IDs require explicitly enabling addition; generated IDs appear in the prepared data before acceptance. The displayed preview contains at most 100 rows / 64 KiB. No dataset asset is changed during preparation. Source file edits afterward do not change this prepared import. Changing settings or switching project requires a fresh preview.

**Accept import** saves that exact prepared content as immutable UTF-8 JSONL and publishes the dataset definition through the journaled project save. Imported files live under the configured datasets directory and are included in project snapshots. Sources are copied, never linked or executed. New UI imports create new datasets; the underlying service also supports expected-hash replacement without changing the dataset ID.

Limits: one million rows, 1 GiB prepared source, 1 MiB serialized JSONL record. JSON-array input is byte-bounded before deserialization; CSV and JSONL input have character bounds. Duplicate-ID tracking grows with row count; row contents are streamed rather than all retained in memory. Dates are date-only conversions, not timestamps. Live sources remain deferred.

Cancellation/parse failure before acceptance preserves project assets. Publication writes a uniquely named source before committing its definition, so a crash or failed definition save can leave an unreferenced source file, but cannot overwrite a prior source. These files are intentionally retained for inspection; automatic orphan garbage collection is not implemented. Prepared temporary spools are deleted when disposed/process handles close.

Verification: 62 dataset/persistence tests passed, including 100,000-row streaming, late errors, duplicate IDs, stale replacements, hash-failure cleanup, snapshots and existing transaction rollback. `--dataset-smoke <output-directory>` verifies desktop conversion, stale-preview rejection and import of the exact prepared source. Manual keyboard/Narrator/high-contrast acceptance and a friendlier mapping editor remain follow-up work.
