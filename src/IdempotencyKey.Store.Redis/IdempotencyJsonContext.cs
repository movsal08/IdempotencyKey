using System.Text.Json.Serialization;
using IdempotencyKey.Core;

namespace IdempotencyKey.Store.Redis;

[JsonSerializable(typeof(IdempotencyResponseSnapshot))]
internal partial class IdempotencyJsonContext : JsonSerializerContext
{
}
