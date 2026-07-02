namespace Sharkable;

/// <summary>
/// Process-wide security configuration for the <c>Sharkable.AutoCrud.SqlSugar</c>
/// plugin. Static properties are read by <see cref="AutoCrudGenerator"/> at the
/// time each entity's routes are generated (during startup). Configure them
/// once, before <c>AddShark()</c> / <c>UseShark()</c> complete.
/// </summary>
public partial class AutoCrudSqlSugar
{
    /// <summary>
    /// When <c>true</c>, every generated CRUD endpoint auto-attaches
    /// <c>.RequireAuthorization()</c> so unauthenticated callers receive 401.
    /// <para>
    /// <b>Default is <c>false</c> for backward compatibility.</b> Set to
    /// <c>true</c> in any production deployment that exposes the AutoCrud
    /// surface to the network. Requires <see cref="SharkOption.EnableAuthorization"/>
    /// (on by default) and a registered authentication scheme, otherwise
    /// <c>RequireAuthorization()</c> rejects every request with 401.
    /// </para>
    /// </summary>
    public static bool AutoCrudRequireAuthorization { get; set; } = false;

    /// <summary>
    /// Maximum allowed <c>page</c> query parameter for AutoCrud list endpoints.
    /// Requests with <c>page &gt; MaxPageNumber</c> receive HTTP 400. The list
    /// handler also rejects requests where <c>(page - 1) * pageSize</c> would
    /// overflow <see cref="int.MaxValue"/>.
    /// <para>
    /// Default: <c>1_000_000</c>. Bounds the worst-case <c>Skip</c> cost so a
    /// single attacker cannot force the DB to scan and discard an unbounded
    /// number of rows (pagination DoS, SHARK-SEC-025).
    /// </para>
    /// </summary>
    public static int MaxPageNumber { get; set; } = 1_000_000;

    /// <summary>
    /// Maximum length, in characters, of any single <c>filter[...]</c> value
    /// supplied to AutoCrud list endpoints. Requests with a longer value
    /// receive HTTP 400 (SHARK-SEC-027).
    /// <para>
    /// Default: <c>200</c>. Bounds the cost of substring / <c>LIKE</c> scans
    /// against large text columns and prevents an attacker from forcing the
    /// DB to scan huge in-memory strings.
    /// </para>
    /// </summary>
    public static int MaxFilterValueLength { get; set; } = 200;

    /// <summary>
    /// Maximum number of comma-separated items accepted in an <c>in</c> or
    /// <c>nin</c> filter. Requests with more items receive HTTP 400
    /// (SHARK-SEC-027).
    /// <para>
    /// Default: <c>100</c>. Bounds the cost of expanding the <c>IN</c>
    /// clause into N placeholders and the resulting DB query-plan / execution
    /// cost.
    /// </para>
    /// </summary>
    public static int MaxInArraySize { get; set; } = 100;
}