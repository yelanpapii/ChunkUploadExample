using System.Text.Json;
using ChunkUploadExample.Models;
using StackExchange.Redis;

public sealed class RedisUploadMetadata : IUploadStore
{
    private readonly IDatabase _database;
    private readonly string _prefix;
    private static readonly LuaScript ReleaseLockScript = LuaScript.Prepare("if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end");

    public RedisUploadMetadata(IConnectionMultiplexer connection, StorageOptions options)
    {
        _database = connection.GetDatabase();
        _prefix = options.Redis.RedisKeyPrefix.TrimEnd(':');
    }

    public string Strategy => "Redis";

    public async Task<bool> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken)
    {
        var key = Key($"upload:{upload.Id}");
        var created = await _database.StringSetAsync(key, Serialize(upload), expiry: null, keepTtl: false, when: When.NotExists);
        if (!created) return false;

        var idempotencyKey = Key($"idempotency:{upload.IdempotencyKey}");
        if (!await _database.StringSetAsync(idempotencyKey, upload.Id.ToString(), expiry: null, keepTtl: false, when: When.NotExists))
        {
            await _database.KeyDeleteAsync(key);
            return false;
        }

        return true;
    }

    public async Task<UploadRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var value = await _database.StringGetAsync(Key($"upload:{id}"));
        return value.IsNullOrEmpty ? null : Deserialize(value!);
    }

    public async Task<UploadRecord?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var id = await _database.StringGetAsync(Key($"idempotency:{idempotencyKey}"));
        return id.IsNullOrEmpty || !Guid.TryParse(id.ToString(), out var uploadId) ? null : await GetAsync(uploadId, cancellationToken);
    }

    public async Task MarkChunkReceivedAsync(Guid id, int chunkIndex, long bytesReceived, CancellationToken cancellationToken)
    {
        var upload = await GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"Upload '{id}' was not found.");
        if (upload.ChunksReceived.Add(chunkIndex)) upload.BytesUploaded += bytesReceived;
        if (upload.Status != UploadStatus.Completed) upload.Status = UploadStatus.InProgress;
        await _database.StringSetAsync(Key($"upload:{id}"), Serialize(upload));
    }

    public async Task MarkCompletedAsync(Guid id, string finalFilePath, long bytesUploaded, UploadCompletionResult result, CancellationToken cancellationToken)
    {
        var upload = await GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"Upload '{id}' was not found.");
        upload.Status = UploadStatus.Completed;
        upload.FinishedAt = DateTimeOffset.UtcNow;
        upload.FinalFilePath = finalFilePath;
        upload.BytesUploaded = bytesUploaded;
        upload.CompletionResult = result;
        await _database.StringSetAsync(Key($"upload:{id}"), Serialize(upload));
    }

    public Task<bool> TryAcquireLockAsync(Guid id, string owner, TimeSpan expiry, CancellationToken cancellationToken) =>
        _database.StringSetAsync(Key($"lock:{id}"), owner, expiry, When.NotExists);

    public async Task ReleaseLockAsync(Guid id, string owner, CancellationToken cancellationToken)
    {
        await _database.ScriptEvaluateAsync(
            ReleaseLockScript.ExecutableScript,
            new[] { (RedisKey)Key($"lock:{id}") },
            new[] { (RedisValue)owner });
    }

    private RedisKey Key(string suffix) => $"{_prefix}:{suffix}";
    private static string Serialize(UploadRecord upload) => JsonSerializer.Serialize(upload);
    private static UploadRecord Deserialize(string value) => JsonSerializer.Deserialize<UploadRecord>(value)!;
}
