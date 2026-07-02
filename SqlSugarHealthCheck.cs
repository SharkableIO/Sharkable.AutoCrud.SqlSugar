using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace Sharkable.AutoCrud.SqlSugar;

/// <summary>
/// Health check that verifies SqlSugar database connectivity. The status
/// returned to <c>/healthz</c> is intentionally generic; the database type,
/// connection details, and raw exception messages are logged for operators
/// only — never exposed on the public endpoint (SHARK-SEC-026).
/// </summary>
public sealed class SqlSugarHealthCheck : IHealthCheck
{
    private readonly ISqlSugarClient _client;
    private readonly ILogger<SqlSugarHealthCheck> _logger;

    /// <summary>
    /// Creates a new <see cref="SqlSugarHealthCheck"/> backed by the given
    /// SqlSugar client.
    /// </summary>
    /// <param name="client">The SqlSugar client used to verify connectivity.</param>
    /// <param name="logger">Logger for diagnostic details (DB type, exception messages).</param>
    public SqlSugarHealthCheck(ISqlSugarClient client, ILogger<SqlSugarHealthCheck> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns generic status to public <c>/healthz</c>. The healthy payload
    /// exposes only a latency measurement — never the database type or
    /// connection info. Failure descriptions are limited to a generic
    /// <c>"Database unreachable"</c>; the underlying <see cref="Exception"/>
    /// is logged at warning level for operators.
    /// </remarks>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var dbType = _client.CurrentConnectionConfig.DbType;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _client.Ado.ExecuteCommandAsync("SELECT 1");
            sw.Stop();

            return HealthCheckResult.Healthy(
                "Database reachable",
                new Dictionary<string, object>
                {
                    ["latencyMs"] = sw.ElapsedMilliseconds,
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SqlSugar health check failed (dbType={DbType}).", dbType);
            return HealthCheckResult.Unhealthy("Database unreachable");
        }
    }
}