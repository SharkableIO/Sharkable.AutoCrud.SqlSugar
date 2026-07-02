# Sharkable.AutoCrud.SqlSugar Security Audit Report

**Audit date:** 2026-07-02
**Package version:** 0.5.4
**Scope:** `/Volumes/Doc/dev/Sharkable.AutoCrud.SqlSugar/` (AutoCrud.SqlSugar plugin package)
**Out of scope:** Core `Sharkable` library (audited separately at `/Volumes/Doc/dev/Sharkable/local/security-audit.md`); `Sharkable.Sample` / `Sharkable.AotSample` reference projects (non-security-relevant sample apps)
**Method:** Read-only static analysis — no code modifications
**Auditor:** Security audit subagent (single-agent pass; SqlSugar/ORM focus)

---

## Executive Summary

The AutoCrud.SqlSugar package is small and focused (3 actual source files of substance plus options/extensions), but the same core-library H-4 default-anonymous pattern applies here, and the **mass-assignment vector is amplified** because AutoCrud auto-generates endpoints that **accept the entire entity body without any field allowlist**. Combined with no default auth, this is an exploitable, dual-purpose Critical finding.

In addition:
- **SafeSoftDeleteField validation (added in 0.5.4)** is correctly wired and is genuine defense-in-depth.
- **Filter / sort field allowlist** (`validFields`) is correctly wired — direct SQL injection via field names is blocked.
- **Health-check** leaks DB type + raw `ex.Message` to public `/healthz`.
- **Pagination DoS** is partially mitigated (MaxPageSize cap on `pageSize`), but unbounded on `page`.

Total findings:

| Severity | Count |
|----------|-------|
| Critical | **1** |
| High     | **5** |
| Medium   | **5** |
| Low      | **6** |

The package version **0.5.4 is current** per `Sharkable.AutoCrud.SqlSugar.csproj:5-6`. The `SafeSoftDeleteField` fix from CHANGELOG `0.5.4` is **in place** and effective.

---

## CRITICAL findings (1)

### C-1 — Mass assignment in `InsertableByObject` / `UpdateableByObject`: entire request body accepted without field allowlist

**File:** `AutoCrudGenerator.cs:118-141`
**Severity:** Critical (privilege escalation, audit-trail forgery, soft-delete bypass, tenant isolation bypass)

```csharp
// POST /
routes.MapPost("/", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync(entityType);
    if (body == null)
        return Results.BadRequest("Request body is required.");
    await _client.InsertableByObject(body).ExecuteCommandAsync();   // <-- whole entity
    return Results.Ok(body);
});

// PUT /{id}
routes.MapPut($"{{{pkName}}}", async (string id, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync(entityType);
    if (body == null)
        return Results.BadRequest("Request body is required.");
    SetPrimaryKey(body, pkName, ConvertKey(id, entityType, pkName));
    await _client.UpdateableByObject(body).ExecuteCommandAsync();   // <-- whole entity
    return Results.Ok(body);
});
```

`ReadFromJsonAsync(entityType)` deserializes every JSON property into the entity. `InsertableByObject(body)` / `UpdateableByObject(body)` then **write every column the type contains**. There is no:
- Allow-list (`[JsonIgnore]` / `[Editable]` / `[CrudIgnore]`-style attribute)
- Block-list (no `WHERE NOT IN (sensitive columns)`)
- Filter on the entity-side write path

**Attack scenarios (each works against a vanilla `ProductEndpoint : ISharkEndpoint, IAutoCrudEntity<Product>`):**

1. **Audit-trail forgery** — if entity has `CreatedBy` / `CreatedAt`:
   ```json
   POST /api/product
   { "Id": 0, "Name": "widget", "CreatedBy": "admin", "CreatedAt": "2020-01-01T00:00:00Z" }
   ```
   The record is persisted with attacker-chosen audit metadata.

2. **Privilege escalation** — if entity has `UserId`, `IsAdmin`, `Role`, `TenantId`, `EmailVerified`:
   ```json
   POST /api/user
   { "Email": "victim@example.com", "UserId": 999, "IsAdmin": true, "EmailVerified": true }
   ```

3. **Soft-delete bypass (cross-cuts M-1 below, escalated to Critical under mass assignment)** — see M-1. With `UpdateableByObject`, an attacker revives any soft-deleted record:
   ```json
   PUT /api/product/123
   { "Name": "widget", "IsDeleted": 0 }
   ```
   `IsDeleted = 1` (set via DELETE earlier) is overwritten back to `0` → record reappears in all `WHERE IsDeleted = 0` reads.

4. **Tenant isolation bypass** — if entity has `TenantId`:
   ```json
   POST /api/order
   { "Item": "secret", "TenantId": "competitor-corp" }
   ```

**Remediation:**

1. **Allow-list** — introduce a marker attribute (e.g. `[CrudAllow]`), allow only marked properties into `Insertable` / `Updateable` calls. Default-deny everything not marked. Example shape:
   ```csharp
   var props = entityType.GetProperties()
       .Where(p => p.GetCustomAttribute<CrudAllowAttribute>() != null);
   var safeEntity = Project(body, props);
   ```

2. **Block-list** at minimum — strip `CreatedAt`/`CreatedBy`/`IsDeleted`/`DeletedAt`/`UpdatedBy`/`UpdatedAt`/`TenantId`/`UserId`/`IsAdmin` before the write call. Document the block-list, or expose it as `SqlSugarOptions.SensitiveFields`.

3. **Alternative**: skip `InsertableByObject` / `UpdateableByObject` and use the **strongly-typed** `Insertable<T>` / `Updateable<T>` overloads with parameter-bound entity instances rather than the dynamic `object` overload. Reflection on `object` is precisely what mass-assignment exploits ride.

4. **Effective immediately (cheap stopgap)**: in `SetPrimaryKey`, also clear sensitive columns:
   ```csharp
   SetPrimaryKey(body, pkName, ConvertKey(id, entityType, pkName));
   foreach (var sens in SensitiveProperties(entityType))
       sens.SetValue(body, sens.PropertyType == typeof(string) ? null : SensibleDefaultFor(sens.PropertyType));
   ```

The Core library has no `RequireAuthorization()` on auto-generated routes (see `EndPointExtension.cs:111-115` and core audit H-4); C-1 is **most severe in combination with H-1** below.

---

## HIGH findings (5)

### H-1 — Auto-generated CRUD endpoints are anonymous by default; no `RequireAuthorization()` is ever invoked

**File:** `AutoCrudGenerator.cs:66-160`
**Severity:** High (unauthenticated data exposure; combined with C-1 → unauthenticated mass assignment)

The `MapGet` / `MapPost` / `MapPut` / `MapDelete` calls inherit the group's endpoint filters (which include `ApiKeyFilter` and `AuthorizationInterceptorFilter` **only when the developer configures them** — see `EndPointExtension.cs:111-115`). Per SharkOption defaults, the developer must explicitly:

```csharp
opt.ApiKeys = new[] {"..."};                              // enables ApiKeyFilter
opt.AuthorizationInterceptorFactory = sp => ...;          // enables AuthorizationInterceptorFilter
opt.ConfigureAuthorization(o => o.FallbackPolicy = ...); // sets global fallback
```

If none of these are configured, **every CRUD endpoint is anonymous**. C-1 + H-1 = any unauthenticated HTTP client can drop a JSON body to `POST /api/<entity>` and create rows with mass-assigned fields.

**Remediation:**
1. In `GenerateRoutes` for `Create` / `Update` / `Delete` operations, add `.RequireAuthorization()` by default; provide an explicit `[AllowAnonymous]` opt-out attribute or `opt.AllowAnonymousCrud = true` flag.
2. Document loudly in README that the AutoCrud endpoints inherit the group's auth configuration (which is **off** by default). Cross-reference core audit H-4 / H-6.

### H-2 — Soft-delete bypass via `UpdateableByObject`

**File:** `AutoCrudGenerator.cs:130-141, 143-161`
**Severity:** High (logical access-control circumvention; bypasses the `SafeSoftDeleteField` defense added in 0.5.4)

```csharp
routes.MapPut($"{{{pkName}}}", async (string id, HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync(entityType);
    if (body == null)
        return Results.BadRequest("Request body is required.");
    SetPrimaryKey(body, pkName, ConvertKey(id, entityType, pkName));
    await _client.UpdateableByObject(body).ExecuteCommandAsync();   // <-- rewrites IsDeleted=0
    return Results.Ok(body);
});
```

The soft-delete column (default `IsDeleted`) is included in `Updateable`'s update set. Even though `ApplySoftDeleteFilter` (line 259-265) filters reads, an attacker:

1. Records any soft-deletable entity's ID (e.g. by listing active ones, `GET /api/<entity>?pageSize=100`).
2. Sends `PUT /api/<entity>/<id>` with body `{"IsDeleted":0}` (or simply re-supplying the entity minus the deletion marker).
3. The soft-deleted record becomes visible to **all subsequent reads**, because `UpdateableByObject` writes `IsDeleted = 0` directly to the row.

The `SafeSoftDeleteField` validation in 0.5.4 only ensures the field name is alphanum+`_` (line 44-46, 48-49). It does **not** prevent an attacker from supplying that field's value in the JSON body.

**Remediation:**
- Either strip `SafeSoftDeleteField` (and `DeletedAt` if present) from the deserialized `body` before calling `Updateable`, or
- Apply an additional `WHERE {SafeSoftDeleteField} = 0` to the `Updateable`'s underlying SQL via `Updateable<object>().AS(tableName).Where(...)`.

### H-3 — Pagination `page` is unbounded — `int.MaxValue` triggers `Skip` overflow

**File:** `AutoCrudGenerator.cs:68-81`
**Severity:** High (DoS; possible SELECT overflow / negative-skip behavior)

```csharp
var page = int.TryParse(ctx.Request.Query["page"], out var p) && p > 0 ? p : 1;
var pageSize = int.TryParse(ctx.Request.Query["pageSize"], out var s) && s > 0
    ? Math.Min(s, _options.MaxPageSize) : _options.DefaultPageSize;
...
var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
```

`p > 0` only checks sign. With `page = int.MaxValue` and `pageSize = 100`, `(page - 1) * pageSize = 2147483646 * 100` overflows `int` to a negative number (or throws `OverflowException` on checked contexts). Different DB drivers react differently:
- **PostgreSQL/MySQL**: negative LIMIT/OFFSET → DB may interpret as huge offset or throw.
- **SQL Server**: throws `Arithmetic overflow`.
- **SqlSugar**: typically surfaces as an exception → unhandled 500 → possible SQL error disclosure.

`MaxPageSize` is correctly applied; the fix has to be on `page`.

**Remediation:**
1. Add `MaxPage` to `SqlSugarOptions` (e.g. `int.MaxValue / _options.MaxPageSize - 1` to keep within safe range).
2. Clamp: `var page = ... Math.Min(p, _options.MaxPage) : 1;`.
3. Return `400 Bad Request` if input is malformed rather than silently defaulting.

### H-4 — `SqlSugarHealthCheck` leaks DB type and raw `ex.Message` to public `/healthz`

**File:** `SqlSugarHealthCheck.cs:23-41` (combined with core `HealthCheckEndpoint` unauthenticated wiring)
**Severity:** High (information disclosure)

```csharp
return HealthCheckResult.Healthy(
    $"SqlSugar connected in {sw.ElapsedMilliseconds}ms",
    new Dictionary<string, object>
    {
        ["latencyMs"] = sw.ElapsedMilliseconds,
        ["dbType"] = _client.CurrentConnectionConfig.DbType.ToString(),
    });
...
return HealthCheckResult.Unhealthy(
    $"SqlSugar connection failed: {ex.Message}");
```

`/healthz` is served unauthenticated by default (per core `SharkOption.EnableHealthChecks = false` — opt-in, but documentation shows it without auth; see core audit M-4). An attacker polling `/healthz` learns:

- **The DB engine** (`Sqlite`, `MySql`, `PostgreSql`, `Oracle`, etc.) via `dbType`. Tells them which DB-specific exploits to try.
- **Failure-mode partial SQL/connection info** via `ex.Message`: `SqlSugar.SqlException: Connect to 10.0.0.5:1433 failed — Login failed for user 'sa'`. SqlSugar's exception messages routinely include the connection-string sanitized version, server address, and error code.

**Remediation:**
1. Never include `dbType` in public health data — gate it behind admin auth.
2. Return generic descriptions on failure: `"database unreachable"`. Move raw `ex.Message` to server-side logs only.
3. Or follow the pattern in core `Middleware/HealthChecks.cs:21-36` (also flagged as M-4) and the corresponding `ConfigurationValidator` advice: expose DB type only when client has valid bearer/API key.
4. Add a `SqlSugarHealthCheckOptions.ExposeDbType = false` default.

### H-5 — `Convert.ChangeType` may throw on invalid ID; 500 leaks stack frames

**File:** `AutoCrudGenerator.cs:279-285`
**Severity:** High (unhandled exceptions on user input)

```csharp
private static object ConvertKey(string id, Type entityType, string pkName)
{
    var prop = entityType.GetProperty(pkName);
    if (prop == null) return id;
    var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
    return Convert.ChangeType(id, targetType);   // <-- throws on bad input
}
```

If `pkType` is `int` and URL is `/api/product/abc`, `Convert.ChangeType("abc", typeof(int))` throws `FormatException`. The map handler has no try/catch, so the exception bubbles up. Per core `ExceptionHandlerOptions:57-60` (core audit M-3), in dev mode `exception.ToString()` (with full SQL stack) is returned to the client; in production it's still a 500 with `ex.Message`.

**Attack scenarios:**
- Resource enumeration: attacker probes `GET /api/<entity>/<various>` to find which routes exist and what PK types are accepted. Brute-forces ID type via 400 (FormatException) vs 404 (NotFound) differentials.
- DB error leakage on conversion-induced partial parsing (e.g. very-long `int`-overflow string).

**Remediation:**
1. Wrap in try/catch; return `Results.BadRequest("Invalid id format.")`.
2. Or use `TypeConverter.ConvertFromString(id)` and fall back gracefully.

---

## MEDIUM findings (5)

### M-1 — `FilterOperator.Like` enables wildcard enumeration via user-supplied `%` / `_`

**File:** `AutoCrudGenerator.cs:229`
**Severity:** Medium (information disclosure / enumeration)

```csharp
FilterOperator.Like => query.Where($"{field} LIKE @v", new { v = value }),
```

`@v` is properly parameterized — **no SQL injection**. But LIKE wildcards in the value are passed through verbatim:
- `?filter[email][like]=%` → matches every email.
- `?filter[reset_token][like]=%` → enumerates all reset tokens (substring-match allowing prefix-recovery).
- `?filter[password_hash][like]=%a%a%a%a%a%` → causes a CPU-bound scan regardless of parameterization.

In addition, `validFields` does not distinguish sensitive vs public columns: `Password`, `ApiToken`, `EmailVerifiedToken`, `PasswordResetToken` etc. are all valid filter targets if a developer exposes them on the entity.

**Remediation:**
1. Escape `%`, `_`, and `\` in the supplied value before binding (SqlSugar's parameter binding does not auto-escape wildcards).
2. Maintain `validFields` with a sub-classification (e.g. "filterable", "sortable", "searchable") or accept `[Searchable]` attribute.
3. Default `FilterOperator.Like` to requiring a minimum non-wildcard prefix (e.g. ≥ 3 chars).

### M-2 — Unbounded `IN` / `NOT IN` array parameter

**File:** `AutoCrudGenerator.cs:230-231`
**Severity:** Medium (memory / CPU DoS)

```csharp
FilterOperator.In  => query.Where($"{field} IN (@v)", new { v = value.Split(',', StringSplitOptions.RemoveEmptyEntries) }),
FilterOperator.Nin => query.Where($"{field} NOT IN (@v)", new { v = value.Split(',', StringSplitOptions.RemoveEmptyEntries) }),
```

A single query string parameter can carry an arbitrarily large CSV. With 100 K elements:
- Server-side split allocates a 100 K-string array.
- SqlSugar expands `(@v)` into 100 K placeholders, each a separate DB parameter.
- DB prepares a 100 K-element `IN` clause → parse/precompile cost grows quadratically on some engines; some DBs (PostgreSQL with very large `IN`) fall back to nested-loop planning.

**Remediation:**
1. Cap array length: `const int MaxInClauseSize = 1000;`. Return `400 Bad Request` if exceeded.
2. Document the cap; surface via `SqlSugarOptions.MaxInClauseSize`.

### M-3 — `AddSqlSugar(null)` silently returns without registering any service

**File:** `Extensions/AutoCrudExtension.cs:14-17, 50-54`
**Severity:** Medium (silent misconfiguration → potential secure-default loss)

```csharp
public static IServiceCollection AddSqlSugar(this IServiceCollection services, Action<SqlSugarOptions>? setupOption = null)
{
    if (setupOption == null)
        return services;          // <-- silent no-op
    ...
    services.AddSingleton<IAutoCrudGenerator, AutoCrudGenerator>();
    services.AddSingleton<IHealthCheck, SqlSugarHealthCheck>();
}
```

Two distinct call paths reach `AddSqlSugar`:
1. **Core library calls it via reflection** (`EndPointExtension.cs:18-26` → `MapSharkEndpoints` → `AddAutoCrud` → reflection-invoked `AddSqlSugar(services, SqlSugarOptionsConfigure)`). When `ConfigureAutoCrud` was called by the developer, the lambda is non-null — registration proceeds. Fine.
2. **Developer calls it directly** with `services.AddSqlSugar()` (no arg) — `setupOption == null` → registration skipped. **Silent failure.** Developer expects endpoints; none are generated. Diagnostic burden: no log, no exception.

Security implications:
- A developer testing with no config may believe CRUD routes exist (per README) and skip auth wiring. Combined with H-1, they never reach H-1's auto-anon exposure because there are no routes — but other tests are misconfigured.
- More importantly: a developer who manually wires a non-standard `AddSqlSugar` flow sees their custom config silently ignored — they think endpoints were generated.

This also means `IAutoCrudGenerator` is **never registered** in this code path; `EndPointExtension.cs:172-192` checks `app.Services.GetService<IAutoCrudGenerator>() != null` and skips auto-generation cleanly — so the no-op is at least safe from a routing perspective. But it is "silent" by design.

**Remediation:**
1. Throw `InvalidOperationException("AddSqlSugar requires a configuration delegate; use opt => { opt.ConnectionString = ...; }")` instead of `return services;`.
2. Or log via `Utils.WriteDebug` at warn level: "AddSqlSugar called with no configuration; skipping." (current code uses `Utils.WriteDebug` for at least one path — apply consistently).

### M-4 — `services.BuildServiceProvider()` invoked in `AddSqlSugar`

**File:** `Extensions/AutoCrudExtension.cs:27-33`
**Severity:** Medium (captive dependency; multiple roots)

```csharp
var provider = services.BuildServiceProvider();
var beforeCfg = provider.GetService<IOptions<ConnectionConfig>>()?.Value;
...
if (beforeCfg != null && beforeCfg.ConnectionString != null)
{
    conf = beforeCfg;
}
```

Building a temporary `ServiceProvider` from `IServiceCollection` is the **captive-dependency anti-pattern**. Side effects:

- **Disposal leak**: the `provider` is never `using`-disposed. Singleton services captured against this root are not eligible for finalizer disposal.
- **Stale state**: any singleton registered up to this point (e.g. `IOptions<ConnectionConfig>` from `BeforeShark`) is read via this provider. If it later mutates (e.g. via reload-on-change), the cached `beforeCfg` reference goes stale.
- **Singleton-while-scoped-in-use**: when the host builds the real `ServiceProvider` later and disposes it, configurations may conflict.

(Note: this technique is used in `GetService` not `GetRequiredService` — if `IOptions<ConnectionConfig>` is not yet registered, returns null safely. No null-deref trap.)

**Remediation:**
1. Use a temporary `ServiceProvider` with `using var scope = services.BuildServiceProvider();` or
2. Replace with `services.GetService<>()` after the final `BuildServiceProvider()` (move the read into `IAutoCrudGenerator` construction).
3. Or — best — eliminate the early-build entirely: check `IOptions<ConnectionConfig>` lazily inside `AutoCrudGenerator` (which is constructed after the real provider exists).

### M-5 — `pageSize = 0` triggers `Math.Ceiling((double)total / pageSize)` → `NaN` / `Infinity`

**File:** `AutoCrudGenerator.cs:68-89`
**Severity:** Medium (DoS on misconfigured `DefaultPageSize`)

```csharp
var pageSize = int.TryParse(ctx.Request.Query["pageSize"], out var s) && s > 0
    ? Math.Min(s, _options.MaxPageSize) : _options.DefaultPageSize;
...
totalPages = (int)Math.Ceiling((double)total / pageSize),
```

If `SqlSugarOptions.DefaultPageSize = 0` and caller omits `pageSize`, `0 / pageSize` produces `NaN`. `Math.Ceiling(NaN) = NaN`. `(int)NaN` throws on most runtimes. **Server-side 500, possible information disclosure.**

Note: `ConfigurationValidator` from core audit L-17 only validates 2 of 15+ options; `SqlSugarOptions.DefaultPageSize` and `MaxPageSize` are not in that validator.

**Remediation:**
1. In `ConfigurationValidator`, enforce `DefaultPageSize >= 1 && DefaultPageSize <= MaxPageSize && MaxPageSize >= 1`.
2. Add `Math.Max(1, pageSize)` defensive cast at use-site.

---

## LOW findings (6)

### L-1 — `Results.Ok(body)` in POST echoes mass-assigned values back to attacker
**File:** `AutoCrudGenerator.cs:120-128`

After `InsertableByObject(body)`, the response is the unmodified `body` — including any attacker-supplied values that the DB silently accepted (e.g. `IsDeleted = 1` reflected back). Combined with C-1, this is the attacker's feedback channel. Mitigated by C-1's allow-list fix.

### L-2 — `ListAll` endpoint permits unrestricted full-table dump
**File:** `AutoCrudGenerator.cs:95-104`

```csharp
routes.MapGet("/all", async () =>
{
    var query = _client.Queryable<object>().AS(tableName).With(SqlWith.NoLock);
    query = ApplySoftDeleteFilter(query, isSoftDeletable);
    var all = await query.ToListAsync();
    return Results.Ok(all);
});
```

Opt-in via `CrudOperations.ListAll` (intentionally excluded from `All`). On a large table (`Order` with 100 M rows), this allocates the entire result set in memory and serializes it as one JSON payload → memory exhaustion / response-time DoS. The README does **not** warn about this.

**Remediation:**
- Add doc note; let the developer own it (opt-in already).

### L-3 — Filter silently ignores invalid fields / unknown operators (no 400 response)
**File:** `AutoCrudGenerator.cs:178-195`

Invalid `filter[field][unknownop]=...` and unknown field names are silently dropped. This:
- Masks attacker error-probing attempts in logs.
- Hides typos in legitimate developer requests.

Returning a 400 with a structured error would improve UX **and** make attack reconnaissance noisier (loud = monitorable).

### L-4 — `InsertableByObject` / `UpdateableByObject` round-trip reflection penalty (perf, not security)
**File:** `AutoCrudGenerator.cs:125, 138`

The `object` overload uses runtime reflection. For large schemas, this is significantly slower than the typed generic overload. Not a security finding per se, but worth noting.

### L-5 — `IsValidFieldName` silently falls back to `DefaultSoftDeleteField = "IsDeleted"` on invalid config
**File:** `AutoCrudGenerator.cs:44-46`

```csharp
private string SafeSoftDeleteField => IsValidFieldName(_options.SoftDeleteFieldName)
    ? _options.SoftDeleteFieldName!
    : DefaultSoftDeleteField;
```

This is **good defense-in-depth** — non-alphanumeric `SoftDeleteFieldName` is rejected and falls back safely. **However**, when fallback occurs, no warning is logged. A developer misconfiguring `SoftDeleteFieldName = "is_deleted"` (valid) vs `"is deleted"` (invalid) silently gets the default. Documentation / ConfigurationValidator should warn. (Note: this is also a side-effect of the soft-delete fix in 0.5.4 working correctly — by design.)

### L-6 — `NuGetAuditMode` / `NU1903` suppressed in package
**File:** `Sharkable.AutoCrud.SqlSugar.csproj:18`

```xml
<NoWarn>$(NoWarn);NU1903</NoWarn>
```

`NU1903` in NuGet is about dependency versions resolving to a different patch than specified. Suppression here is downstream-inherited — anyone consuming this package sees the same suppression in their build output. Supply-chain hygiene: explicitly enumerate the suppressed codes, or move the suppression into a `<NoWarn>` for the build only.

NU1903 is **not** a vulnerability audit code (that would be `NU1902`), so this is low-impact.

---

## OPEN QUESTION: ambiguity requiring human review

1. **`SafeSoftDeleteField` in delete** (line 151): the raw SQL is `$"UPDATE {tableName} SET {SafeSoftDeleteField} = 1 WHERE {pkName} = @id"`. The `SafeSoftDeleteField` is interpolated directly into the SQL — protection is the alphanum+`_` allowlist on the developer-supplied option. **Reviewer should confirm** whether the validation `char.IsLetterOrDigit(c) || c == '_'` covers all real-world DB column names (yes for ASCII, possibly insufficient for Unicode-named DB columns — extremely rare). If the entity has a column like `isdeleted_mycompany$special`, that's rejected. That's acceptable.

2. **AOT-compatibility of `InsertableByObject`**: the analyzer `Sharkable.Analyzers/AutoCrudAotPreserver.cs` is meant to preserve entity types, but `InsertableByObject` on `object` uses runtime type discovery. **NativeAOT runtime risk**: if the analyzer misses a type, `InsertableByObject(body)` will throw at runtime. **Reviewer should run `Sharkable.NativeTest` against `PublishAot=true`** to confirm no runtime type-discovery errors.

3. **`GetPrimaryKeyName` fallback to `entityType.GetProperty("Id")`** (line 275): if a developer marks multiple `[SugarColumn(IsPrimaryKey = true)]` or none at all, the code picks the first PK or falls back to literal `Id`. **Multi-PK entities** silently get wrong behavior. Not security per se but worth noting.

4. **`Convert.ChangeType` for unsupported types** (line 284): `Guid`, `DateTime`, `byte[]` (binary) — `Convert.ChangeType` on a string-to-byte[] would throw `InvalidCastException`. Idempotent with H-5.

5. **`InsertableByObject` with `Id` not in JSON body**: SqlSugar auto-increments `Id` typically. But if `Id` is NOT auto-increment and not in JSON, the row is inserted with `Id = 0` (or default). Not exploitable but worth noting.

---

## TOP-10 REMEDIATION PRIORITY

If prioritizing fixes, address in this order:

1. **C-1** (Critical) — Allow-list / block-list entity properties before `InsertableByObject` / `UpdateableByObject`; stop mass-assignment.
2. **H-1** (High) — Add `RequireAuthorization()` default on Create / Update / Delete operations; allow `[AllowAnonymous]` opt-out.
3. **H-2** (High) — Strip `SafeSoftDeleteField` (and `DeletedAt` if present) from deserialized body before `UpdateableByObject`.
4. **H-3** (High) — Clamp `page` to `MaxPage`; return `400` on malformed numeric inputs.
5. **H-4** (High) — Stop returning `dbType` and `ex.Message` from `SqlSugarHealthCheck` to public `/healthz`; gate via auth.
6. **H-5** (High) — Try/catch around `Convert.ChangeType`; return `400 Bad Request` on conversion failure.
7. **M-1** (Medium) — Escape LIKE wildcards in `FilterOperator.Like`.
8. **M-2** (Medium) — Cap `IN` / `NOT IN` array size.
9. **M-3** (Medium) — Replace silent `if (setupOption == null) return services;` with `throw` / log.
10. **M-4** (Medium) — Eliminate premature `services.BuildServiceProvider()`.

---

## Recently-issued fixes (post-0.5.3)

- ✅ v0.5.4: `SafeSoftDeleteField` validation added (alphanum + `_` allowlist). Wire-checked in `AutoCrudGenerator.cs:44-49, 151, 263`. Correctly defends against SQL injection via `SoftDeleteFieldName`.
- ✅ v0.5.4: `SqlSugarOptions.MaxPageSize` / `DefaultPageSize` configurable. Enforced at line 70. **However, `page` (not `pageSize`) is still unbounded — flagged H-3.**
- ❗ v0.5.4 still missing: mass-assignment field allow-list (the largest open risk).
- ❗ v0.5.4 still missing: `RequireAuthorization()` default on CRUD (compounded with C-1).
- ❗ v0.5.4 still missing: LIKE wildcard escaping and `IN` clause size cap.
- ❗ v0.5.4 still missing: health-check info-leak hardening.

---

## File-by-file severity rollup

| File | Critical | High | Medium | Low |
|------|---------:|-----:|-------:|----:|
| `AutoCrudGenerator.cs` | 1 | 3 | 2 | 3 |
| `Extensions/AutoCrudExtension.cs` | 0 | 0 | 2 | 0 |
| `SqlSugarHealthCheck.cs` | 0 | 1 | 0 | 0 |
| `AutoCrudSqlSugar.cs` (empty partial) | 0 | 0 | 0 | 0 |
| `Consts/AutoCrud.cs` | 0 | 0 | 0 | 0 |
| `GlobalUsings.cs` | 0 | 0 | 0 | 0 |
| `README.md` | 0 | 0 | 0 | 0 |
| `Sharkable.AutoCrud.SqlSugar.csproj` | 0 | 0 | 0 | 1 |
| `Sharkable.AutoCrud.SqlSugar.nuspec` | 0 | 0 | 0 | 0 |

**Clean files (no findings):**
- `AutoCrudSqlSugar.cs` — empty partial-class placeholder. No risk.
- `Consts/AutoCrud.cs` — single `public const string ServiceName`. No risk.
- `GlobalUsings.cs` — three `global using` directives. No risk.
- `README.md` — documentation; no executable risk in user-facing content.
- `Sharkable.AutoCrud.SqlSugar.nuspec` — version metadata. No risk.

---

**Report end. No code modifications were made.**
