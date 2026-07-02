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
}