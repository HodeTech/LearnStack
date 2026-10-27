namespace LearnStack.SharedKernel.Permissions;

public enum PermissionScope { Platform = 1, Tenant = 2, Organization = 3 }

public enum BuiltInRoleKey
{
    TenantAdmin = 1, Instructor = 2, Learner = 3, Guardian = 4, Editor = 5,
    OrgAdmin = 6, OrgInstructor = 7,
}

/// <summary>A scoped capability definition; registration alone grants no authority.</summary>
public sealed record PermissionDefinition
{
    public PermissionDefinition(PermissionScope scope, string key, bool isActive = true)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        ArgumentNullException.ThrowIfNull(key);
        var segments = key.Split('.');
        if (segments.Length != 3 || !IsSegment(segments[0]) || !IsSegment(segments[1])
            || segments[2] is not ("read" or "write" or "delete" or "admin"))
            throw new ArgumentException("Permission definition is invalid.", nameof(key));
        Scope = scope;
        Key = key;
        IsActive = isActive;
    }

    public PermissionScope Scope { get; }
    public string Key { get; }
    public bool IsActive { get; }

    private static bool IsSegment(string value) => value.Length > 0 && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

/// <summary>Finite operation metadata, independent of seed or live admission.</summary>
public interface IIdentityPermissionRequest
{
    PermissionDefinition RequiredPermission { get; }
}
