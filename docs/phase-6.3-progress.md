# Phase 6.3 Progress — Transactional Postman/OpenAPI/cURL/HAR Import and Supported Exports

**Date:** 2026-11-08 22:15 IST  
**Issue:** [Issue #29](https://github.com/git-maniam/API-Wrangler/issues/29)  
**Parent Epic:** [Issue #26](https://github.com/git-maniam/API-Wrangler/issues/26)  
**Milestone:** Milestone B — Complete required platform  
**Branch:** `phase6-import-export`  

---

## 1. Summary of Changes

1. **cURL Command Parser (`CurlParser`):**
   - Pure string argument tokenization (handles single/double quotes, escape characters, line continuations).
   - Never executes cURL, shells, or external processes.
   - Extracts method (`-X`, `--request`), URL, headers (`-H`, `--header`, `-A`, `-b`, `-e`), data/body (`-d`, `--data`, `--data-raw`, `--data-binary`), basic auth (`-u`, `--user`), multipart form data (`-F`, `--form`), and transport flags (`-L` follow redirects, `-k` insecure TLS).
   - Generates fully populated `HttpRequestDefinition`.

2. **Postman Collection v2.1 & Environment Importers (`PostmanImporter`):**
   - Recursively parses folder hierarchies, requests, headers, query parameters, auth configurations (basic, bearer, apiKey), and request bodies (raw, urlencoded, formdata).
   - Extracts pre-request and test scripts into `PreRequestScript` and `TestScript`.
   - Scans scripts using `PostmanCompatibilityService` to identify and report unsupported Postman features.
   - Detects sensitive variables in collections and environments, generating actionable guidance to extract credentials into `.local/` local secrets rather than committing to Git.

3. **Postman Exporters (`PostmanExporter`):**
   - Exports native collections, folders, and requests into compatible Postman Collection v2.1 schema format.
   - Round-trip fidelity: Imported Postman collections exported and re-imported preserve request names, methods, URLs, headers, bodies, and scripts.
   - Exports environment definitions with secrets redacted (`value = ""`), producing an export audit report noting redacted variables.

4. **OpenAPI 3.0/3.1 Importer (`OpenApiImporter`):**
   - Parses OpenAPI 3.0/3.1 JSON specifications.
   - Maps endpoint paths and operations (`GET`, `POST`, `PUT`, `DELETE`, `PATCH`, `HEAD`, `OPTIONS`) to requests.
   - Groups operations into folders by OpenAPI tags.
   - Extracts query parameters, header parameters, request body schemas, and server base URLs into a collection `baseUrl` variable.

5. **HAR 1.2 Importer (`HarImporter`):**
   - Parses HTTP Archive 1.2 entries (`log.entries`).
   - Extracts method, target URL, headers, query string parameters, and `postData` (raw or multi-part params).
   - Structures requests into an imported collection.

6. **Transactional Import Service (`ProjectImportService`):**
   - Full 10-step transactional workflow conforming to Section 13.2:
     1. Read source without executing it.
     2. Parse format (auto-detects Postman, OpenAPI, cURL, HAR).
     3. Calculate entity counts (Requests, Collections, Folders, Environments, Variables).
     4. Detect sensitive secrets requiring local storage.
     5. Report unsupported features.
     6. Preview proposed asset files.
     7. Stage into isolated in-memory edits.
     8. Validate references and schemas with `AssetValidator`.
     9. Atomically publish into `IProjectSession` using journaled transaction writer lease.
     10. Save import report to `reports/import-report-<timestamp>.json`.
   - Guarantees failed imports never partially overwrite or corrupt valid project assets.

7. **Desktop Integration:**
   - Added `ProjectImportViewModel` in `ApiWrangler.Desktop`.
   - Added interactive "Import & export" tab in `MainWindow.xaml` with source browse, paste text editor, format selector, entity count summaries, secret guidance, unsupported feature notices, preview file list, and atomic publish button.

8. **Automated Verification:**
   - Added `ProjectImportExportTests.cs` (9 comprehensive integration tests).
   - All 227 tests in the solution passed cleanly.
   - Desktop smoke test passed (`scripts/Test-Desktop.ps1`).
   - Code formatting verified (`dotnet format --verify-no-changes`).

