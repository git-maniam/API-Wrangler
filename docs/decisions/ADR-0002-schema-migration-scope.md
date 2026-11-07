# ADR-0002: Schema migration scope for the first project release

Status: accepted for Phase 1.

## Decision

Schema version 1 is the first supported API Wrangler project format. There are no supported legacy project inputs in Phase 1: no pre-v1 API Wrangler format has been published, and Postman import belongs to Phase 6. An absent, malformed, or older `schemaVersion` is a validation error, not a migration trigger. Opening and fetching a project never alter its schema version.

When a later schema version is introduced, its migration must name the exact source version and changed fields. Before applying it, show a file-by-file preview and create an immutable, host-local backup of every original file with hashes. Require an explicit user action, validate the complete result, and use the existing journaled transaction for replacement. Retrying an already migrated project must make no changes. Keep the backup on failure and report its location. Do not store backups in the Git working tree or include local secrets in shared migration metadata.

## Alternatives and consequences

Accepting arbitrary unversioned JSON would require guessing its meaning and could overwrite unrelated files. Implementing a generic migration engine without a defined source and destination would not provide a testable conversion. Both are deferred until a concrete version transition exists.

Phase 1 tests verify invalid versions are rejected and newer versions remain inspectable but read-only. Migration preview and backup execution become acceptance criteria for the first release that actually changes the project schema. This decision narrows the P1.1/P1.2 migration criterion; it does not claim that a migration was performed.
