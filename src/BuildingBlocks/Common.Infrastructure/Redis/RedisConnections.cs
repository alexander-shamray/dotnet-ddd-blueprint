namespace Common.Infrastructure.Redis;

/// <summary>
/// The keyed-service names for §8.1's two connections — cache and
/// coordination, separate because the eviction policies cannot be shared.
/// The values are spelled exactly like the configuration keys
/// (<c>ConnectionStrings:RedisCache</c>, §14.1), so one name means one
/// connection in the container, the configuration and the Compose file alike.
/// </summary>
public static class RedisConnections
{
    public const string Cache = "RedisCache";
    public const string Coordination = "RedisCoordination";
}
