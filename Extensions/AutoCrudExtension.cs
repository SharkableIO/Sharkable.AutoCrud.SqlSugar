using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Sharkable.AutoCrud.SqlSugar;

public static class AutoCrudExtension
{
    /// <summary>
    /// Registers SqlSugar client, the <see cref="IAutoCrudGenerator"/>,
    /// and a database health check. Call before <c>AddShark()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="setupOption">
    /// Configuration delegate. Required: a null delegate silently skips
    /// registration, leaving no <see cref="ISqlSugarClient"/> /
    /// <see cref="IAutoCrudGenerator"/> available to the route generator.
    /// SHARK-SEC-M003 throws <see cref="InvalidOperationException"/> instead so
    /// misconfigured setups fail loud at startup rather than producing a
    /// silently-empty AutoCrud surface.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="setupOption"/> is <c>null</c>.
    /// </exception>
    public static IServiceCollection AddSqlSugar(this IServiceCollection services, Action<SqlSugarOptions>? setupOption = null)
    {
        if (setupOption == null)
            throw new InvalidOperationException(
                "AddSqlSugar requires a configuration delegate; pass `opt => { opt.ConnectionString = \"...\"; }` " +
                "(SHARK-SEC-M003).");

        var option = new SqlSugarOptions();
        services.Configure<SqlSugarOptions>(opt =>
        {
            setupOption.Invoke(opt);
        });
        setupOption.Invoke(option);
        StaticConfig.EnableAot = Shark.SharkOption.AotMode;

        // SHARK-SEC-M004: build the temporary service provider once and dispose
        // it via `using` to avoid the captive-dependency anti-pattern. The
        // prior code called `services.BuildServiceProvider()` twice without
        // disposal, leaking singleton finalizers and risking stale-state reads.
        using var tempProvider = services.BuildServiceProvider();

        // SHARK-SEC-023: log a startup warning when generated CRUD endpoints will
        // ship anonymous. Default false preserves backward compat, but production
        // deployments MUST set AutoCrudSqlSugar.AutoCrudRequireAuthorization = true
        // (opt-in flag) so every generated endpoint auto-attaches
        // RequireAuthorization(). AOT apps don't need this — the reflection path
        // that auto-discover routes is off, so routes only exist when the developer
        // explicitly opted in.
        if (!AutoCrudSqlSugar.AutoCrudRequireAuthorization && !Shark.SharkOption.AotMode)
        {
            var logger = tempProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("Sharkable.AutoCrud.SqlSugar.AddSqlSugar");
            logger?.LogWarning(
                "AutoCrudSqlSugar.AutoCrudRequireAuthorization is false — generated CRUD endpoints will be anonymous. " +
                "Set AutoCrudSqlSugar.AutoCrudRequireAuthorization = true before deploying to production (SHARK-SEC-023).");
        }

        var beforeCfg = tempProvider.GetService<IOptions<ConnectionConfig>>()?.Value;

        ConnectionConfig conf;
        if (beforeCfg != null && beforeCfg.ConnectionString != null)
        {
            conf = beforeCfg;
        }
        else
        {
            conf = new ConnectionConfig
            {
                IsAutoCloseConnection = option.IsAutoCloseConnection,
                DbType = (global::SqlSugar.DbType)option.DbType,
                ConnectionString = option.ConnectionString,
                ConfigId = option.ConfigId,
                InitKeyType = (global::SqlSugar.InitKeyType)option.InitKeyType,
                DbLinkName = option.DbLinkName,
                LanguageType = (global::SqlSugar.LanguageType)option.LanguageType,
                IndexSuffix = option.IndexSuffix,
            };
        }

        SqlSugarScope sqlSugar = new(conf);
        services.AddKeyedSingleton<ISqlSugarClient>(AutoCrudSqlSugar.ServiceName, sqlSugar);
        services.AddSingleton<ISqlSugarClient>(sqlSugar);
        services.AddSingleton<IAutoCrudGenerator, AutoCrudGenerator>();
        services.AddSingleton<IHealthCheck, SqlSugarHealthCheck>();

        return services;
    }

    /// <summary>
    /// Registers a connection config before Sharkable services.
    /// </summary>
    public static IServiceCollection BeforeShark(this IServiceCollection services, Action<ConnectionConfig>? setupConfig)
    {
        services.Configure<ConnectionConfig>(opt =>
        {
            setupConfig?.Invoke(opt);
        });
        return services;
    }
}
