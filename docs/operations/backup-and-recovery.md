# Foundation data and recovery

The current desktop does not save projects or editor buffers. Unsaved request edits are lost when it closes. Project persistence, atomic saves, recovery journals, and migrations belong to phase 1 and are not implemented yet.

Complete response bodies are stored under `%LOCALAPPDATA%/ApiWrangler/runs` by default. They are raw local data, may contain sensitive response contents, and have no automatic retention yet. The CLI accepts `--output` to choose an explicit artifact directory. Cancelled, failed, or size-limited requests remove their partial body files under normal filesystem conditions.

Do not place raw artifacts in Git. Archive only deliberately selected evidence. The foundation has no secret vault or operational database to back up yet.

Before production release, add and verify interrupted-write recovery, user settings recovery, database-plus-artifact backups, safe schema migrations, rollback, and restoration drills. Never automatically replay a mutating API request during recovery.
