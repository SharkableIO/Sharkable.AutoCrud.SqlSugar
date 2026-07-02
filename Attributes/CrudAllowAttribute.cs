namespace Sharkable;

/// <summary>
/// Marks a property on an AutoCrud entity as client-writable for insert and update
/// operations exposed by the generated <c>POST /</c> and <c>PUT /&#123;id&#125;</c> endpoints.
/// 
/// <para>
/// <b>Default-deny.</b> Properties on an AutoCrud entity that are NOT annotated with this
/// attribute are excluded from the auto-generated write paths, regardless of what the JSON
/// request body contains. Sensitive audit and identity columns (<c>CreatedBy</c>,
/// <c>CreatedAt</c>, <c>UpdatedBy</c>, <c>UpdatedAt</c>, <c>IsDeleted</c>, <c>IsAdmin</c>,
/// <c>TenantId</c>, <c>UserId</c>, <c>EmailVerified</c>, etc.) MUST be explicitly opted in
/// with this attribute if they are intended to be set via the API — by default they are
/// silently rejected, blocking mass-assignment privilege escalation (SHARK-SEC-006).
/// </para>
/// 
/// <para>
/// The primary key is always handled by the route URL (<c>PUT /&#123;id&#125;</c>) or by the
/// database auto-increment (<c>POST /</c>) and is excluded from this attribute's effect.
/// </para>
/// 
/// <para>
/// If <c>Create</c> or <c>Update</c> is enabled on an entity that has zero
/// <see cref="CrudAllowAttribute"/>-decorated properties, route generation throws
/// <see cref="InvalidOperationException"/> at startup rather than silently exposing an
/// endpoint that writes nothing.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class CrudAllowAttribute : Attribute
{
}