# Sharkable.AutoCrud.SqlSugar

Automatic CRUD API generation for [Sharkable](https://github.com/SharkableIO/Sharkable) with [SqlSugar](https://github.com/DotNetNext/SqlSugar) ORM.

## Install

```bash
dotnet add package Sharkable.AutoCrud.SqlSugar
```

## Usage

### Step 1: Configure DB

```csharp
builder.Services.AddShark(opt =>
{
    opt.ConfigureAutoCrud(s =>
    {
        s.DbType = DbType.Sqlite;
        s.ConnectionString = "DataSource=app.db";
    });
});
```

### Step 2: Define entity + endpoint

```csharp
[SugarTable("products")]
public class Product
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public class ProductEndpoint : ISharkEndpoint, IAutoCrudEntity<Product>
{
    // Empty — all 5 CRUD operations auto-generated
}
```

**Auto-generated routes** (`api/product`):

| Method | Route | Operation |
|--------|-------|-----------|
| `GET` | `/` | List all |
| `GET` | `/{id}` | Get by ID |
| `POST` | `/` | Create |
| `PUT` | `/{id}` | Update |
| `DELETE` | `/{id}` | Delete |

### Suppress Operations

```csharp
public class ReadOnlyEndpoint : ISharkEndpoint, IAutoCrudEntity<Product>
{
    CrudOperations IAutoCrudEntity<Product>.AllowedOperations =>
        CrudOperations.List | CrudOperations.Get;
    // POST/PUT/DELETE suppressed
}
```

### Custom Override

Write your own route in `AddRoutes` — it takes precedence over auto-generated:

```csharp
public class ProductEndpoint : ISharkEndpoint, IAutoCrudEntity<Product>
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Override the auto-generated GET list
        app.MapGet("/", async (ISqlSugarClient db) =>
        {
            var list = await db.Queryable<Product>()
                .Where(p => p.Price > 0)
                .OrderBy(p => p.Name)
                .ToListAsync();
            return Results.Ok(list);
        });
    }
}
```

## Health Check

SqlSugar database connectivity is automatically checked via `/healthz` (when `EnableHealthChecks = true`):

```json
{
  "checks": {
    "SqlSugar": {
      "status": "healthy",
      "description": "SqlSugar connected in 3ms",
      "data": { "latencyMs": 3, "dbType": "Sqlite" }
    }
  }
}
```

## License

MIT
