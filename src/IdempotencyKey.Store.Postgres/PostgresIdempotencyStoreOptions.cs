using Npgsql;

namespace IdempotencyKey.Store.Postgres;

public class PostgresIdempotencyStoreOptions
{
    public string? ConnectionString { get; set; }
    public NpgsqlDataSource? DataSource { get; set; }
    public string Schema { get; set; } = "public";
    public string TableName { get; set; } = "idempotency_records";
    public int? CommandTimeoutSeconds { get; set; }
    public bool EnableEnsureCreated { get; set; }
}
