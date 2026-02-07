using StackExchange.Redis;

namespace IdempotencyKey.Store.Redis;

public class RedisIdempotencyStoreOptions
{
    /// <summary>
    /// The connection string to Redis.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// An existing ConnectionMultiplexer instance. If set, this takes precedence over Configuration.
    /// </summary>
    public IConnectionMultiplexer? ConnectionMultiplexer { get; set; }

    /// <summary>
    /// An existing ConnectionMultiplexer factory. If set, this takes precedence over Configuration.
    /// </summary>
    public Func<Task<IConnectionMultiplexer>>? ConnectionMultiplexerFactory { get; set; }

    /// <summary>
    /// The prefix for Redis keys. Defaults to "idem:".
    /// </summary>
    public string KeyPrefix { get; set; } = "idem:";

    /// <summary>
    /// The database index to use.
    /// </summary>
    public int? Database { get; set; }
}
