using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Sharkable.AutoCrud.SqlSugar;

/// <summary>
/// CRUD route generator for SqlSugar-backed entities.
/// Registered automatically by <c>AddSqlSugar()</c>.
/// </summary>
public sealed class AutoCrudGenerator : IAutoCrudGenerator
{
    private readonly ISqlSugarClient _client;
    private readonly SqlSugarOptions _options;

    private const string DefaultSoftDeleteField = "IsDeleted";

    private static readonly Dictionary<string, FilterOperator> OperatorMap = new()
    {
        ["eq"] = FilterOperator.Eq,
        ["ne"] = FilterOperator.Ne,
        ["gt"] = FilterOperator.Gt,
        ["gte"] = FilterOperator.Gte,
        ["lt"] = FilterOperator.Lt,
        ["lte"] = FilterOperator.Lte,
        ["like"] = FilterOperator.Like,
        ["in"] = FilterOperator.In,
        ["nin"] = FilterOperator.Nin,
        ["null"] = FilterOperator.Null,
    };

    public AutoCrudGenerator(ISqlSugarClient client)
        : this(client, new SqlSugarOptions()) { }

    public AutoCrudGenerator(ISqlSugarClient client, IOptions<SqlSugarOptions> options)
        : this(client, options.Value) { }

    private AutoCrudGenerator(ISqlSugarClient client, SqlSugarOptions options)
    {
        _client = client;
        _options = options;
        // SHARK-SEC-M005: validate pagination options at startup so a
        // misconfigured DefaultPageSize/MaxPageSize cannot silently turn
        // `totalPages = (int)Math.Ceiling((double)total / pageSize)` into
        // NaN/Infinity and surface as an unhandled 500 on every list call.
        if (_options.DefaultPageSize < 1)
            throw new InvalidOperationException(
                $"SqlSugarOptions.DefaultPageSize must be >= 1 (was {_options.DefaultPageSize}). " +
                "Set opt.DefaultPageSize in AddSqlSugar (SHARK-SEC-M005).");
        if (_options.MaxPageSize < 1)
            throw new InvalidOperationException(
                $"SqlSugarOptions.MaxPageSize must be >= 1 (was {_options.MaxPageSize}). " +
                "Set opt.MaxPageSize in AddSqlSugar (SHARK-SEC-M005).");
        if (_options.DefaultPageSize > _options.MaxPageSize)
            throw new InvalidOperationException(
                $"SqlSugarOptions.DefaultPageSize ({_options.DefaultPageSize}) must be <= MaxPageSize ({_options.MaxPageSize}) " +
                "(SHARK-SEC-M005).");
    }

    private string SafeSoftDeleteField => IsValidFieldName(_options.SoftDeleteFieldName)
        ? _options.SoftDeleteFieldName!
        : DefaultSoftDeleteField;

    private static bool IsValidFieldName(string? name)
        => !string.IsNullOrEmpty(name) && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    public void GenerateRoutes(IEndpointRouteBuilder routes, Type entityType,
        Type endpointType, CrudOperations operations)
    {
        if (operations == CrudOperations.None)
            return;

        var tableName = entityType.Name;
        var pkName = GetPrimaryKeyName(entityType);
        var isSoftDeletable = entityType.GetInterfaces().Any(i => i.Name == "ISoftDeletable");
        var validFields = new HashSet<string>(entityType.GetProperties().Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        // SHARK-SEC-006 + SHARK-SEC-024: explicit field allow-list for mass-assignment
        // defense. Only properties carrying [CrudAllow] are written from the JSON
        // body; the primary key is excluded (URL-bound on PUT, DB-generated on
        // POST), and the soft-delete column is excluded even if marked [CrudAllow]
        // so attackers cannot revive soft-deleted rows by sending `IsDeleted = false`.
        //
        // Defense-in-depth (SHARK-SEC-024): GetCrudAllowedColumns excludes the
        // soft-delete column by BOTH the resolved PropertyInfo (attribute / column-
        // rename aware, layer 1) AND a case-insensitive name match against the
        // configured SafeSoftDeleteField (layer 2). The second layer guarantees the
        // column is excluded even if layer-1 resolution somehow misses (e.g. an
        // entity without [SugarColumn] rename where the C# property happens to be
        // named the same as the configured SoftDeleteFieldName).
        var softDeleteProp = GetSoftDeleteProperty(entityType);
        var allowedWriteColumns = GetCrudAllowedColumns(entityType, pkName, softDeleteProp, SafeSoftDeleteField);
        var ignoredWriteColumns = GetIgnoredWriteColumns(entityType, pkName, allowedWriteColumns);

        if ((operations.HasFlag(CrudOperations.Create) || operations.HasFlag(CrudOperations.Update))
            && allowedWriteColumns.Length == 0)
        {
            throw new InvalidOperationException(
                $"AutoCrud entity '{entityType.FullName}' has no [CrudAllow] properties. " +
                $"Mark at least one non-primary-key property with [CrudAllow] before enabling " +
                $"Create/Update operations. This prevents a silent mass-assignment rejection " +
                $"endpoint (SHARK-SEC-006).");
        }

        // SHARK-SEC-023: opt-in authorization. Auto-attach RequireAuthorization()
        // to every CRUD endpoint when AutoCrudSqlSugar.AutoCrudRequireAuthorization
        // is true. Defaults to false (backward compat). Production deployments
        // MUST enable this — otherwise the auto-generated CRUD surface is
        // anonymous, and combined with mass-assignment risk becomes unauthenticated
        // data exposure / privilege escalation.
        static IEndpointConventionBuilder RequireAuth(IEndpointConventionBuilder b)
            => AutoCrudSqlSugar.AutoCrudRequireAuthorization
                ? b.RequireAuthorization()
                : b;

        // GET / — paginated list
        if (operations.HasFlag(CrudOperations.List))
        {
            var b = routes.MapGet("/", async (HttpContext ctx) =>
            {
                var page = int.TryParse(ctx.Request.Query["page"], out var p) && p > 0 ? p : 1;
                var pageSize = int.TryParse(ctx.Request.Query["pageSize"], out var s) && s > 0
                    ? Math.Min(s, _options.MaxPageSize) : _options.DefaultPageSize;
                // SHARK-SEC-M005: defensive clamp — even though the constructor
                // validates DefaultPageSize/MaxPageSize >= 1, Math.Max(1, ...)
                // keeps this method independently safe if it is ever moved or
                // invoked outside the validated constructor path.
                pageSize = Math.Max(1, pageSize);

                // SHARK-SEC-025: bound `page` to prevent pagination DoS.
                // (page - 1) * pageSize can overflow int.MaxValue with large page,
                // which then surfaces as a DB-side arithmetic error or, on some
                // drivers, an unexpectedly huge negative OFFSET that scans the
                // full table before discarding rows.
                var maxPage = AutoCrudSqlSugar.MaxPageNumber;
                if (page > maxPage)
                    return Results.BadRequest($"page must be <= {maxPage}.");
                if (pageSize > 0 && (long)(page - 1) * pageSize > int.MaxValue)
                    return Results.BadRequest("(page - 1) * pageSize overflows int.MaxValue.");

                var query = _client.Queryable<object>().AS(tableName).With(SqlWith.NoLock);
                query = ApplySoftDeleteFilter(query, isSoftDeletable);
                var (q, filterError) = ApplyFilters(query, ctx.Request.Query, validFields);
                if (filterError != null) return Results.BadRequest(filterError);
                query = q;

                var sortRaw = ctx.Request.Query["sort"].ToString();
                if (!string.IsNullOrWhiteSpace(sortRaw))
                    query = ApplySort(query, sortRaw, validFields);

                var total = await query.Clone().CountAsync();
                var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

                return Results.Ok(new
                {
                    items,
                    total,
                    page,
                    pageSize,
                    totalPages = (int)Math.Ceiling((double)total / pageSize),
                });
            });
            RequireAuth(b);
        }

        // GET /all — full-table dump (only when ListAll is explicitly enabled)
        // SHARK-SEC-L002: opting into `CrudOperations.ListAll` is a deliberate
        // developer choice and is NOT enabled by the default `All` flag set.
        // The /all endpoint materializes the entire table into a single JSON
        // response — on a large table (Order with 100M+ rows) this can exhaust
        // memory and stall the request thread. Use only for small reference
        // tables (lookup data, country codes, etc.) and pair with
        // AutoCrudRequireAuthorization.
        if (operations.HasFlag(CrudOperations.ListAll))
        {
            var b = routes.MapGet("/all", async () =>
            {
                var query = _client.Queryable<object>().AS(tableName).With(SqlWith.NoLock);
                query = ApplySoftDeleteFilter(query, isSoftDeletable);
                var all = await query.ToListAsync();
                return Results.Ok(all);
            });
            RequireAuth(b);
        }

        if (operations.HasFlag(CrudOperations.Get) && pkName != null)
        {
            var b = routes.MapGet($"{{{pkName}}}", async (string id) =>
            {
                var pk = ConvertKey(id, entityType, pkName);
                var q = _client.Queryable<object>().AS(tableName).With(SqlWith.NoLock);
                q = ApplySoftDeleteFilter(q, isSoftDeletable);
                var entity = await q.Where($"{pkName} = @id", new { id = pk }).FirstAsync();
                return entity != null ? Results.Ok(entity) : Results.NotFound();
            });
            RequireAuth(b);
        }

        if (operations.HasFlag(CrudOperations.Create))
        {
            var b = routes.MapPost("/", async (HttpContext ctx) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync(entityType);
                if (body == null)
                    return Results.BadRequest("Request body is required.");
                var identity = await _client.InsertableByObject(body)
                    .IgnoreColumns(ignoredWriteColumns)
                    .ExecuteReturnIdentityAsync();
                var saved = await ReadPersistedEntityAsync(entityType, tableName, pkName, body, identity);
                return Results.Ok(saved ?? body);
            });
            RequireAuth(b);
        }

        if (operations.HasFlag(CrudOperations.Update) && pkName != null)
        {
            var b = routes.MapPut($"{{{pkName}}}", async (string id, HttpContext ctx) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync(entityType);
                if (body == null)
                    return Results.BadRequest("Request body is required.");
                var pk = ConvertKey(id, entityType, pkName);
                SetPrimaryKey(body, pkName, pk);
                await _client.UpdateableByObject(body)
                    .UpdateColumns(allowedWriteColumns)
                    .ExecuteCommandAsync();
                var saved = await ReadEntityByPrimaryKeyAsync(entityType, tableName, pkName!, pk);
                return Results.Ok(saved ?? body);
            });
            RequireAuth(b);
        }

        if (operations.HasFlag(CrudOperations.Delete) && pkName != null)
        {
            var b = routes.MapDelete($"{{{pkName}}}", async (string id) =>
            {
                var pk = ConvertKey(id, entityType, pkName);
                if (isSoftDeletable)
                {
                    await _client.Ado.ExecuteCommandAsync(
                        $"UPDATE {tableName} SET {SafeSoftDeleteField} = 1 WHERE {pkName} = @id",
                        new { id = pk });
                }
                else
                {
                    await _client.Deleteable<object>().AS(tableName)
                        .Where($"{pkName} = @id", new { id = pk }).ExecuteCommandAsync();
                }
                return Results.Ok();
            });
            RequireAuth(b);
        }
    }

    private static (ISugarQueryable<object> Query, string? Error) ApplyFilters(
        ISugarQueryable<object> query,
        IQueryCollection queryParams,
        HashSet<string> validFields)
    {
        var filterMap = new Dictionary<string, List<(FilterOperator Op, string Value)>>();
        var maxLen = AutoCrudSqlSugar.MaxFilterValueLength;
        var maxIn = AutoCrudSqlSugar.MaxInArraySize;

        foreach (var kv in queryParams)
        {
            var key = kv.Key;
            if (key == "page" || key == "pageSize" || key == "sort" || key == "all")
                continue;

            // filter[field] = value (exact match)
            if (key.StartsWith("filter[") && key.EndsWith("]") && !key.Contains("]["))
            {
                var field = key[7..^1];
                if (!validFields.Contains(field)) continue;
                var value = kv.Value.ToString();
                // SHARK-SEC-027: bound per-value length.
                if (value.Length > maxLen)
                    return (query, $"filter[{field}] value length {value.Length} exceeds MaxFilterValueLength ({maxLen}).");
                AddFilter(filterMap, field, FilterOperator.Eq, value);
            }
            // filter[field][op] = value
            else if (key.StartsWith("filter[") && key.Contains("]["))
            {
                var closeFirst = key.IndexOf(']');
                var field = key[7..closeFirst];
                var opStart = closeFirst + 2;
                var opEnd = key.Length - 1;
                var opStr = key[opStart..opEnd];

                if (!validFields.Contains(field) || !OperatorMap.TryGetValue(opStr, out var op)) continue;
                var value = kv.Value.ToString();
                // SHARK-SEC-027: bound per-value length.
                if (value.Length > maxLen)
                    return (query, $"filter[{field}][{opStr}] value length {value.Length} exceeds MaxFilterValueLength ({maxLen}).");
                AddFilter(filterMap, field, op, value);
            }
        }

        foreach (var (field, conditions) in filterMap)
        {
            foreach (var (op, value) in conditions)
            {
                // SHARK-SEC-027: bound IN / NOT IN array size. Re-check at apply
                // time so the cap holds even if multiple values target the same
                // field (the per-key check above doesn't catch aggregate size).
                if (op is FilterOperator.In or FilterOperator.Nin)
                {
                    var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > maxIn)
                        return (query, $"filter[{field}][{op}] array size {parts.Length} exceeds MaxInArraySize ({maxIn}).");
                }
                query = ApplyOperator(query, field, op, value);
            }
        }

        return (query, null);
    }

    private static void AddFilter(
        Dictionary<string, List<(FilterOperator, string)>> map,
        string field, FilterOperator op, string value)
    {
        if (!map.TryGetValue(field, out var list))
            map[field] = list = [];
        list.Add((op, value));
    }

    private static ISugarQueryable<object> ApplyOperator(
        ISugarQueryable<object> query, string field, FilterOperator op, string value)
    {
        return op switch
        {
            FilterOperator.Eq => query.Where($"{field} = @v", new { v = value }),
            FilterOperator.Ne => query.Where($"{field} <> @v", new { v = value }),
            FilterOperator.Gt => query.Where($"{field} > @v", new { v = value }),
            FilterOperator.Gte => query.Where($"{field} >= @v", new { v = value }),
            FilterOperator.Lt => query.Where($"{field} < @v", new { v = value }),
            FilterOperator.Lte => query.Where($"{field} <= @v", new { v = value }),
            // SHARK-SEC-027: escape SQL LIKE wildcards (%, _, \) and use the
            // backslash as the SQL ESCAPE character so wildcards supplied by
            // an attacker match literally instead of expanding the scan set.
            FilterOperator.Like => query.Where($"{field} LIKE @v ESCAPE '\\'", new { v = EscapeLike(value) }),
            FilterOperator.In => query.Where($"{field} IN (@v)", new { v = value.Split(',', StringSplitOptions.RemoveEmptyEntries) }),
            FilterOperator.Nin => query.Where($"{field} NOT IN (@v)", new { v = value.Split(',', StringSplitOptions.RemoveEmptyEntries) }),
            FilterOperator.Null => bool.TryParse(value, out var isNull) && isNull
                ? query.Where($"{field} IS NULL")
                : query.Where($"{field} IS NOT NULL"),
            _ => query,
        };
    }

    /// <summary>
    /// Escapes SQL <c>LIKE</c> wildcards (<c>%</c>, <c>_</c>, <c>\</c>) in a
    /// user-supplied filter value so they match literally when used with the
    /// backslash <c>ESCAPE</c> clause in <see cref="ApplyOperator"/>. SHARK-SEC-027.
    /// </summary>
    private static string EscapeLike(string value)
        => value.Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_");

    private static ISugarQueryable<object> ApplySort(
        ISugarQueryable<object> query, string sortRaw, HashSet<string> validFields)
    {
        foreach (var segment in sortRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var desc = trimmed.StartsWith('-');
            var field = trimmed.TrimStart('-', '+');

            if (!validFields.Contains(field)) continue;

            query = desc
                ? query.OrderBy($"{field} DESC")
                : query.OrderBy($"{field} ASC");
        }
        return query;
    }

    private ISugarQueryable<object> ApplySoftDeleteFilter(
        ISugarQueryable<object> query, bool isSoftDeletable)
    {
        return isSoftDeletable
            ? query.Where($"{SafeSoftDeleteField} = 0")
            : query;
    }

    private static string? GetPrimaryKeyName(Type entityType)
    {
        foreach (var prop in entityType.GetProperties())
        {
            if (prop.GetCustomAttributes(typeof(SugarColumn), true)
                    .FirstOrDefault() is SugarColumn attr && attr.IsPrimaryKey)
                return prop.Name;
        }
        var idProp = entityType.GetProperty("Id");
        return idProp?.Name;
    }

    private static object ConvertKey(string id, Type entityType, string pkName)
    {
        var prop = entityType.GetProperty(pkName);
        if (prop == null) return id;
        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        return Convert.ChangeType(id, targetType);
    }

    private static void SetPrimaryKey(object entity, string pkName, object value)
    {
        entity.GetType().GetProperty(pkName)?.SetValue(entity, value);
    }

    /// <summary>
    /// Re-reads the row persisted by the POST handler so the response reflects what is
    /// actually in the database rather than the user-controlled request body. For
    /// identity-keyed entities, the new ID comes from <c>ExecuteReturnIdentityAsync</c>;
    /// for client-assigned keys (GUID, manual int, etc.) the body's PK value is used.
    /// Returns <c>null</c> only when no primary key is configured (in which case the
    /// caller falls back to returning the original body).
    /// </summary>
    private async Task<object?> ReadPersistedEntityAsync(
        Type entityType, string tableName, string? pkName, object body, int identity)
    {
        if (pkName == null)
            return null;
        object pkValue;
        if (identity > 0)
        {
            var pkProp = entityType.GetProperty(pkName)
                ?? throw new InvalidOperationException(
                    $"AutoCrud entity '{entityType.FullName}' has no property for primary key '{pkName}'.");
            pkValue = Convert.ChangeType(identity, pkProp.PropertyType);
        }
        else
        {
            var pkProp = entityType.GetProperty(pkName);
            pkValue = pkProp?.GetValue(body) ?? throw new InvalidOperationException(
                $"AutoCrud POST returned no identity for '{entityType.FullName}' and the body has no '{pkName}' value to re-read.");
        }
        return await ReadEntityByPrimaryKeyAsync(entityType, tableName, pkName, pkValue);
    }

    /// <summary>
    /// Reads a single row from <paramref name="tableName"/> by primary key. Returns
    /// <c>null</c> when no row matches (e.g. PK points to a soft-deleted record while a
    /// soft-delete filter is in effect on the read query).
    /// </summary>
    private async Task<object?> ReadEntityByPrimaryKeyAsync(
        Type entityType, string tableName, string pkName, object pkValue)
    {
        var q = _client.Queryable<object>().AS(tableName).With(SqlWith.NoLock);
        return await q.Where($"{pkName} = @id", new { id = pkValue }).FirstAsync();
    }

    /// <summary>
    /// Returns the C# property names of columns decorated with <see cref="CrudAllowAttribute"/>,
    /// excluding the primary key and the configured soft-delete column. Used as the explicit
    /// allow-list for SqlSugar's <c>UpdateColumns(...)</c> call on the PUT path
    /// (SHARK-SEC-006).
    /// </summary>
    /// <param name="softDeleteProp">
    /// The entity property mapped to the soft-delete column, or <c>null</c> if the entity has
    /// no such property. This property is excluded from the allow-list even if marked
    /// <see cref="CrudAllowAttribute"/>, so an attacker cannot revive soft-deleted records by
    /// sending <c>IsDeleted = false</c> in the PUT body.
    /// </param>
    /// <param name="safeSoftDeleteField">
    /// The validated <see cref="SqlSugarOptions.SoftDeleteFieldName"/> (default
    /// <c>"IsDeleted"</c>). Defense-in-depth (SHARK-SEC-024): any property whose name
    /// matches this value case-insensitively is also excluded. This second layer
    /// protects against an attacker reviving a soft-deleted row via a property name
    /// that slipped past the <paramref name="softDeleteProp"/> resolution (e.g. an
    /// entity whose C# property name exactly equals the configured soft-delete field
    /// but is missing a <c>[SugarColumn]</c> rename the resolver looks for).
    /// </param>
    private static string[] GetCrudAllowedColumns(
        Type entityType, string? pkName, PropertyInfo? softDeleteProp, string safeSoftDeleteField)
    {
        var allowed = new List<string>();
        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (pkName != null && string.Equals(prop.Name, pkName, StringComparison.Ordinal))
                continue;
            if (softDeleteProp != null &&
                string.Equals(prop.Name, softDeleteProp.Name, StringComparison.Ordinal))
                continue;
            if (string.Equals(prop.Name, safeSoftDeleteField, StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.GetCustomAttribute<CrudAllowAttribute>(inherit: true) != null)
                allowed.Add(prop.Name);
        }
        return allowed.ToArray();
    }

    /// <summary>
    /// Locates the C# property mapped to <see cref="SqlSugarOptions.SoftDeleteFieldName"/>.
    /// Honors <see cref="SugarColumn.ColumnName"/> renames. Returns <c>null</c> when the
    /// entity has no such property (e.g. non-soft-deletable entities).
    /// </summary>
    private PropertyInfo? GetSoftDeleteProperty(Type entityType)
    {
        var fieldName = SafeSoftDeleteField;
        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var columnName = prop.GetCustomAttribute<SugarColumn>(inherit: true)?.ColumnName;
            if (string.IsNullOrEmpty(columnName))
                columnName = prop.Name;
            if (string.Equals(columnName, fieldName, StringComparison.OrdinalIgnoreCase))
                return prop;
        }
        return null;
    }

    /// <summary>
    /// Returns the column names that must be excluded from <c>InsertableByObject</c>'s write
    /// path. This is the complement of <see cref="GetCrudAllowedColumns"/> against the entity's
    /// mapped properties (the primary key is always ignored — the DB auto-generates it).
    /// Used as the explicit ignore-list for SqlSugar's <c>IgnoreColumns(...)</c> call on the
    /// POST path (SHARK-SEC-006).
    /// </summary>
    private static string[] GetIgnoredWriteColumns(Type entityType, string? pkName,
        IReadOnlyCollection<string> allowedColumns)
    {
        var allowedSet = new HashSet<string>(allowedColumns, StringComparer.Ordinal);
        var ignored = new List<string>();
        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (pkName != null && string.Equals(prop.Name, pkName, StringComparison.Ordinal))
            {
                ignored.Add(prop.Name);
                continue;
            }
            if (!allowedSet.Contains(prop.Name))
                ignored.Add(prop.Name);
        }
        return ignored.ToArray();
    }
}
