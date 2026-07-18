using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Sharkable;

namespace Sharkable.AutoCrud.SqlSugar;

/// <summary>
/// AutoCrud SqlSugar plugin — auto-discovered by Sharkable at startup.
/// Enables automatic CRUD route generation for SqlSugar-backed entities.
/// Must be paired with <c>services.AddSqlSugar(opt => { ... })</c> called before <c>AddShark()</c>.
/// </summary>
public sealed class AutoCrudPlugin : ISharkPlugin
{
    /// <inheritdoc />
    public string Name => "Sharkable.AutoCrud.SqlSugar";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, SharkOption option)
    {
        // Service registration is handled by AddSqlSugar() called before AddShark().
        // The plugin's role is discovery — Sharkable core finds this implementation
        // and invokes its lifecycle hooks instead of using reflection by name.
    }

    /// <inheritdoc />
    public void ConfigurePipeline(WebApplication app, SharkOption option)
    {
        // CRUD routes are generated during endpoint mapping by the core framework.
    }

    /// <inheritdoc />
    public void ConfigureOpenApi(OpenApiOptions openApiOptions, SharkOption option)
    {
        // No additional OpenAPI transforms needed.
    }
}
