# Changelog

All notable changes to Sharkable.AutoCrud.SqlSugar are documented here.

## [Unreleased]

### security

- **BREAKING**: Add `[CrudAllow]` attribute for explicit field allowlist on AutoCrud insert/update — properties without `[CrudAllow]` are excluded from `Insertable.IgnoreColumns` / `Updateable.UpdateColumns`. Endpoints with `Create | Update` enabled and zero `[CrudAllow]` properties now throw `InvalidOperationException` at startup with the entity name and remediation guidance. Prevents mass-assignment privilege escalation via JSON body (SHARK-SEC-006)
- **BREAKING (SHARK-SEC-006 follow-up)**: Exclude the configured soft-delete column (`SqlSugarOptions.SoftDeleteFieldName`, default `"IsDeleted"`) from the `[CrudAllow]` allow-list even when explicitly marked — otherwise an attacker can revive soft-deleted rows by sending the field in a PUT body. Honors `[SugarColumn(ColumnName = "...")]` renames
- **BREAKING (SHARK-SEC-006 follow-up)**: AutoCrud `POST /` and `PUT /{id}` now return the persisted row re-read from the database instead of the user-controlled request body — previous behavior silently hid server-side defaults (timestamps, identity-generated PK, server-set soft-delete state) from the client