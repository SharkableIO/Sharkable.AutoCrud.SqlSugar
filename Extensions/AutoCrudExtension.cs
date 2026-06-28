using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Sharkable.AutoCrud.SqlSugar;

public static class AutoCrudExtension
{
    /// <summary>
    /// Registers SqlSugar client, the <see cref="IAutoCrudGenerator"/>,
    /// and a database health check. Call before <c>AddShark()</c>.
    /// </summary>
    public static IServiceCollection AddSqlSugar(this IServiceCollection services, Action<SqlSugarOptions>? setupOption = null)
    {
        if (setupOption == null)
            return services;

        var option = new SqlSugarOptions();
        services.Configure<SqlSugarOptions>(opt =>
        {
            setupOption?.Invoke(opt);
        });
        setupOption?.Invoke(option);
        StaticConfig.EnableAot = Shark.SharkOption.AotMode;

        var provider = services.BuildServiceProvider();
        var beforeCfg = provider.GetService<IOptions<ConnectionConfig>>()?.Value;

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
