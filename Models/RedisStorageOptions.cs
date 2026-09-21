public sealed class RedisStorageOptions
{
    public string RedisConnection { get; set; } = "localhost:6379";
    public string RedisKeyPrefix { get; set; } = "chunk-upload";
}
