using ChunkUploadExample.Models;

public interface IUploadStore
{
    string Strategy { get; }
    Task<bool> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken);
    Task<UploadRecord?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<UploadRecord?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task MarkChunkReceivedAsync(Guid id, int chunkIndex, long bytesReceived, CancellationToken cancellationToken);
    Task MarkCompletedAsync(Guid id, string finalFilePath, long bytesUploaded, UploadCompletionResult result, CancellationToken cancellationToken);
    Task<bool> TryAcquireLockAsync(Guid id, string owner, TimeSpan expiry, CancellationToken cancellationToken);
    Task ReleaseLockAsync(Guid id, string owner, CancellationToken cancellationToken);
}