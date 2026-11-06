# Postman compatibility

No Postman importer, exporter, or `pm.*` scripting adapter is implemented in this foundation increment. No compatibility claim is made.

Phase 6 must add fixture-backed support for Collection v2.1, environments, and the APIs listed in specification section 12.5. Every fixture will record supported, partial, or unsupported behavior; unsupported authentication, script APIs, and lossy conversions must produce actionable diagnostics before execution/export.

Do not execute imported scripts or install packages during open/import. Preserve source provenance and validate imports in a temporary transaction before publishing assets.
