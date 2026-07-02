# Changelog

All notable changes to Sharkable.AutoCrud.SqlSugar are documented here.

## [Unreleased]

### security

- **BREAKING**: Add `[CrudAllow]` attribute for explicit field allowlist on AutoCrud insert/update — properties without `[CrudAllow]` are excluded from `Insertable.IgnoreColumns` / `Updateable.UpdateColumns`. Endpoints with `Create | Update` enabled and zero `[CrudAllow]` properties now throw `InvalidOperationException` at startup with the entity name and remediation guidance. Prevents mass-assignment privilege escalation via JSON body (SHARK-SEC-006)
- **BREAKING (SHARK-SEC-006 follow-up)**: Exclude the configured soft-delete column (`SqlSugarOptions.SoftDeleteFieldName`, default `"IsDeleted"`) from the `[CrudAllow]` allow-list even when explicitly marked — otherwise an attacker can revive soft-deleted rows by sending the field in a PUT body. Honors `[SugarColumn(ColumnName = "...")]` renames
- **BREAKING (SHARK-SEC-006 follow-up)**: AutoCrud `POST /` and `PUT /{id}` now return the persisted row re-read from the database instead of the user-controlled request body — previous behavior silently hid server-side defaults (timestamps, identity-generated PK, server-set soft-delete state) from the client
- Add `AutoCrudSqlSugar.AutoCrudRequireAuthorization` opt-in flag — auto-attach `.RequireAuthorization()` to every generated CRUD endpoint when `true`. Default `false` preserves backward compat, but production deployments MUST enable. `AddSqlSugar()` logs a `LogWarning` at startup when the flag is `false` in non-AOT mode (SHARK-SEC-023, cross-repo with `Sharkable`)
- Defense-in-depth: also exclude `SafeSoftDeleteField` from the `[CrudAllow]` allow-list by case-insensitive name match — protects entities whose C# property name matches the configured soft-delete column but lacks a `[SugarColumn]` rename (SHARK-SEC-024, cross-repo with `Sharkable`)
- Add `AutoCrudSqlSugar.MaxPageNumber` (default 1M) + overflow check on `(page - 1) * pageSize` — prevent pagination DoS via `page=int.MaxValue` producing a negative OFFSET that scans the full table (SHARK-SEC-025, cross-repo with `Sharkable`)
- Redact `SqlSugarHealthCheck` description — never expose `dbType` or `ex.Message` on public `/healthz`; full diagnostic detail (DB type, exception) logged at `LogWarning` for operators only (SHARK-SEC-026, cross-repo with `Sharkable`)
- Escape SQL `LIKE` wildcards (`%`, `_`, `\`) with `ESCAPE '\'` clause + cap filter value length via `AutoCrudSqlSugar.MaxFilterValueLength` (default 200) + cap `IN` / `NOT IN` array size via `AutoCrudSqlSugar.MaxInArraySize` (default 100) in AutoCrud search — prevent LIKE wildcard DoS and large-`IN` clause DoS (SHARK-SEC-027, cross-repo with `Sharkable`)