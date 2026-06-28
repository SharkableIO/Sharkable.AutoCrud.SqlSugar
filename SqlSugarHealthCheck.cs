using Microsoft.Extensions.Diagnostics.HealthChecks;
using SqlSugar;

namespace Sharkable.AutoCrud.SqlSugar;

/// <summary>
/// Health check that verifies SqlSugar database connectivity.
/// Registered automatically by <c>AddSqlSugar()</c>.
/// </summary>
public sealed class SqlSugarHealthCheck : IHealthCheck
{
    private readonly ISqlSugarClient _client;

    public SqlSugarHealthCheck(ISqlSugarClient client)
    {
        _client = client;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _client.Ado.ExecuteCommandAsync("SELECT 1");
            sw.Stop();

            return HealthCheckResult.Healthy(
                $"SqlSugar connected in {sw.ElapsedMilliseconds}ms",
                new Dictionary<string, object>
                {
                    ["latencyMs"] = sw.ElapsedMilliseconds,
                    ["dbType"] = _client.CurrentConnectionConfig.DbType.ToString(),
                });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"SqlSugar connection failed: {ex.Message}");
        }
    }
}
