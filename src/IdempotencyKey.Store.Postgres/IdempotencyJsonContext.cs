using System.Text.Json.Serialization;
using IdempotencyKey.Core;

namespace IdempotencyKey.Store.Postgres;

[JsonSerializable(typeof(IdempotencyResponseSnapshot))]
internal partial class IdempotencyJsonContext : JsonSerializerContext
{
}
