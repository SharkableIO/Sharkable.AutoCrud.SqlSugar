using System.Reflection;
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

        // SHARK-SEC-006: explicit field allow-list for mass-assignment defense.
        // Only properties carrying [CrudAllow] are written from the JSON body; the
        // primary key is excluded (URL-bound on PUT, DB-generated on POST).
        var allowedWriteColumns = GetCrudAllowedColumns(entityType, pkName);
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

        // GET / — paginated list
        if (operations.HasFlag(CrudOperations.List))
        {
            routes.MapGet("/", async (HttpContext ctx) =>
            {
                var page = int.TryParse(ctx.Request.Query["page"], out var p) && p > 0 ? p : 1;
                var pageSize = int.TryParse(ctx.Request.Query["pageSize"], out var s) && s > 0
                    ? Math.Min(s, _options.MaxPageSize) : _options.DefaultPageSize;

                var query = _client.Queryable<object>().AS(tableName).With(SqlWith.NoLock);
                query = ApplySoftDeleteFilter(query, isSoftDeletable);
                query = ApplyFilters(query, ctx.Request.Query, validFields);

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
        }

        // GET /all — full-table dump (only when ListAll is explicitly enabled)
        if (operations.HasFlag(CrudOperations.ListAll))
        {
            routes.MapGet("/all", async () =>
            {
                var query = _client.Queryable<object>().AS(tableName).With(SqlWith.NoLock);
                query = ApplySoftDeleteFilter(query, isSoftDeletable);
                var all = await query.ToListAsync();
                return Results.Ok(all);
            });
        }

        if (operations.HasFlag(CrudOperations.Get) && pkName != null)
        {
            routes.MapGet($"{{{pkName}}}", async (string id) =>
            {
                var pk = ConvertKey(id, entityType, pkName);
                var q = _client.Queryable<object>().AS(tableName).With(SqlWith.NoLock);
                q = ApplySoftDeleteFilter(q, isSoftDeletable);
                var entity = await q.Where($"{pkName} = @id", new { id = pk }).FirstAsync();
                return entity != null ? Results.Ok(entity) : Results.NotFound();
            });
        }

        if (operations.HasFlag(CrudOperations.Create))
        {
            routes.MapPost("/", async (HttpContext ctx) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync(entityType);
                if (body == null)
                    return Results.BadRequest("Request body is required.");
                await _client.InsertableByObject(body)
                    .IgnoreColumns(ignoredWriteColumns)
                    .ExecuteCommandAsync();
                return Results.Ok(body);
            });
        }

        if (operations.HasFlag(CrudOperations.Update) && pkName != null)
        {
            routes.MapPut($"{{{pkName}}}", async (string id, HttpContext ctx) =>
            {
                var body = await ctx.Request.ReadFromJsonAsync(entityType);
                if (body == null)
                    return Results.BadRequest("Request body is required.");
                SetPrimaryKey(body, pkName, ConvertKey(id, entityType, pkName));
                await _client.UpdateableByObject(body)
                    .UpdateColumns(allowedWriteColumns)
                    .ExecuteCommandAsync();
                return Results.Ok(body);
            });
        }

        if (operations.HasFlag(CrudOperations.Delete) && pkName != null)
        {
            routes.MapDelete($"{{{pkName}}}", async (string id) =>
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
        }
    }

    private static ISugarQueryable<object> ApplyFilters(
        ISugarQueryable<object> query,
        IQueryCollection queryParams,
        HashSet<string> validFields)
    {
        var filterMap = new Dictionary<string, List<(FilterOperator Op, string Value)>>();

        foreach (var kv in queryParams)
        {
            var key = kv.Key;
            if (key == "page" || key == "pageSize" || key == "sort" || key == "all")
                continue;

            // filter[field] = value (exact match)
            if (key.StartsWith("filter[") && key.EndsWith("]") && !key.Contains("]["))
            {
                var field = key[7..^1];
                if (validFields.Contains(field))
                    AddFilter(filterMap, field, FilterOperator.Eq, kv.Value.ToString());
            }
            // filter[field][op] = value
            else if (key.StartsWith("filter[") && key.Contains("]["))
            {
                var closeFirst = key.IndexOf(']');
                var field = key[7..closeFirst];
                var opStart = closeFirst + 2;
                var opEnd = key.Length - 1;
                var opStr = key[opStart..opEnd];

                if (validFields.Contains(field) && OperatorMap.TryGetValue(opStr, out var op))
                    AddFilter(filterMap, field, op, kv.Value.ToString());
            }
        }

        foreach (var (field, conditions) in filterMap)
        {
            foreach (var (op, value) in conditions)
            {
                query = ApplyOperator(query, field, op, value);
            }
        }

        return query;
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
            FilterOperator.Like => query.Where($"{field} LIKE @v", new { v = value }),
            FilterOperator.In => query.Where($"{field} IN (@v)", new { v = value.Split(',', StringSplitOptions.RemoveEmptyEntries) }),
            FilterOperator.Nin => query.Where($"{field} NOT IN (@v)", new { v = value.Split(',', StringSplitOptions.RemoveEmptyEntries) }),
            FilterOperator.Null => bool.TryParse(value, out var isNull) && isNull
                ? query.Where($"{field} IS NULL")
                : query.Where($"{field} IS NOT NULL"),
            _ => query,
        };
    }

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
    /// Returns the C# property names of columns decorated with <see cref="CrudAllowAttribute"/>,
    /// excluding the primary key. Used as the explicit allow-list for SqlSugar's
    /// <c>UpdateColumns(...)</c> call on the PUT path (SHARK-SEC-006).
    /// </summary>
    private static string[] GetCrudAllowedColumns(Type entityType, string? pkName)
    {
        var allowed = new List<string>();
        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (pkName != null && string.Equals(prop.Name, pkName, StringComparison.Ordinal))
                continue;
            if (prop.GetCustomAttribute<CrudAllowAttribute>(inherit: true) != null)
                allowed.Add(prop.Name);
        }
        return allowed.ToArray();
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
