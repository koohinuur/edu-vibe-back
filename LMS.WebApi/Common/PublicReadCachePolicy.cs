namespace LMS.WebApi.Common;

/// <summary>
/// Names + duration for the anonymous marketing-read output cache. The policy
/// itself is configured with the built-in builder in <c>Program.cs</c>
/// (<c>AddOutputCache</c>); endpoints opt in with
/// <c>[OutputCache(PolicyName = PublicReadCacheHeaderPolicy.Name)]</c>. Kept as a
/// tiny constants holder so the policy name is a single source of truth shared by
/// the registration and every annotated endpoint.
/// </summary>
public static class PublicReadCacheHeaderPolicy
{
    public const string Name = "PublicRead";

    public const int DurationSeconds = 60;
}
