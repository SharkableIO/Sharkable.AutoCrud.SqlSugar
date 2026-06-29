using Microsoft.AspNetCore.Routing;
using SqlSugar;

namespace Sharkable.AutoCrud.SqlSugar;

/// <summary>
/// CRUD route generator for SqlSugar-backed entities.
/// Registered automatically by <c>AddSqlSugar()</c>.
/// </summary>
public sealed class AutoCrudGenerator : IAutoCrudGenerator
{
    private readonly ISqlSugarClient _client;

    public AutoCrudGenerator(ISqlSugarClient client)
    {
        _client = client;
    }

    public void GenerateRoutes(IEndpointRouteBuilder routes, Type entityType,
        Type endpointType, CrudOperations operations)
    {
        if (operations == CrudOperations.None)
            return;

        var tableName = entityType.Name;
        var pkName = GetPrimaryKeyName(entityType);

        // GET /{group} — paginated (default) or full dump (ListAll + ?all=true)
        if (operations.HasFlag(CrudOperations.List) || operations.HasFlag(CrudOperations.ListAll))
        {
            routes.MapGet("/", async (HttpContext ctx) =>
            {
                // Full dump — only when ListAll is explicitly enabled AND ?all=true
                if (operations.HasFlag(CrudOperations.ListAll) &&
                    ctx.Request.Query["all"] == "true")
                {
                    var all = await _client.Queryable<object>().AS(tableName).ToListAsync();
                    return Results.Ok(all);
                }

                // Safe paginated list (default)
                var page = int.TryParse(ctx.Request.Query["page"], out var p) && p > 0 ? p : 1;
                var pageSize = int.TryParse(ctx.Request.Query["pageSize"], out var s) && s > 0
                    ? Math.Min(s, 100) : 20;

                var total = await _client.Queryable<object>().AS(tableName).CountAsync();
                var items = await _client.Queryable<object>().AS(tableName)
                    .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

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

        if (operations.HasFlag(CrudOperations.Get) && pkName != null)
        {
            routes.MapGet($"{{{pkName}}}", async (string id) =>
            {
                var pk = ConvertKey(id, entityType, pkName);
                var entity = await _client.Queryable<object>().AS(tableName)
                    .Where($"{pkName} = @id", new { id = pk }).FirstAsync();
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
                await _client.InsertableByObject(body).ExecuteCommandAsync();
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
                await _client.UpdateableByObject(body).ExecuteCommandAsync();
                return Results.Ok(body);
            });
        }

        if (operations.HasFlag(CrudOperations.Delete) && pkName != null)
        {
            routes.MapDelete($"{{{pkName}}}", async (string id) =>
            {
                var pk = ConvertKey(id, entityType, pkName);
                await _client.Deleteable<object>().AS(tableName)
                    .Where($"{pkName} = @id", new { id = pk }).ExecuteCommandAsync();
                return Results.Ok();
            });
        }
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
}
