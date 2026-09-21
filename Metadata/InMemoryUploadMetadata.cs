using ChunkUploadExample.Models;
using System.Collections.Concurrent;

public class InMemoryUploadMetadata : IUploadStore
{
    private readonly ConcurrentDictionary<Guid, UploadRecord> _uploads = new();
    private readonly ConcurrentDictionary<string, Guid> _uploadsByIdempotencyKey = new(StringComparer.Ordinal);
    public string Strategy => "InMemory";

    public Task<bool> TryCreateAsync(UploadRecord upload, CancellationToken cancellationToken)
    {
        if (!_uploadsByIdempotencyKey.TryAdd(upload.IdempotencyKey, upload.Id))
        {
            return Task.FromResult(false);
        }

        if (!_uploads.TryAdd(upload.Id, upload))
        {
            _uploadsByIdempotencyKey.TryRemove(upload.IdempotencyKey, out _);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task<UploadRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_uploads.TryGetValue(id, out var u) ? u : null);

    public Task<UploadRecord?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_uploadsByIdempotencyKey.TryGetValue(idempotencyKey, out var id) && _uploads.TryGetValue(id, out var upload) ? upload : null);

    public Task MarkChunkReceivedAsync(Guid id, int chunkIndex, long bytesReceived, CancellationToken cancellationToken)
    {
        if (!_uploads.TryGetValue(id, out var upload)) return Task.CompletedTask;
        lock (upload)
        {
            if (upload.ChunksReceived.Add(chunkIndex))
            {
                upload.BytesUploaded += bytesReceived;
            }

            if (upload.Status is not UploadStatus.Completed)
            {
                upload.Status = UploadStatus.InProgress;
            }
        }
        return Task.CompletedTask;
    }

    public Task MarkCompletedAsync(Guid id, string finalFilePath, long bytesUploaded, UploadCompletionResult result, CancellationToken cancellationToken)
    {
        if (_uploads.TryGetValue(id, out var upload))
        {
            lock (upload)
            {
                upload.Status = UploadStatus.Completed;
                upload.FinishedAt = DateTimeOffset.UtcNow;
                upload.FinalFilePath = finalFilePath;
                upload.BytesUploaded = bytesUploaded;
                upload.CompletionResult = result;
            }
        }

        return Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<Guid, string> _locks = new();

    public Task<bool> TryAcquireLockAsync(Guid id, string owner, TimeSpan expiry, CancellationToken cancellationToken) =>
        Task.FromResult(_locks.TryAdd(id, owner));

    public Task ReleaseLockAsync(Guid id, string owner, CancellationToken cancellationToken)
    {
        if (_locks.TryGetValue(id, out var current) && current == owner)
        {
            _locks.TryRemove(id, out _);
        }

        return Task.CompletedTask;
    }

}

public sealed class LocalChunkStorage : IChunkStorage
{
    private readonly ILocalUploadPaths _paths;

    public LocalChunkStorage(ILocalUploadPaths paths) => _paths = paths;

    public string Strategy => "Local";

    public async Task WriteChunkAsync(Guid uploadId, int chunkIndex, Stream content, long expectedLength, CancellationToken cancellationToken)
    {
        var directory = _paths.ChunkDir(uploadId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, chunkIndex.ToString());
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";

        await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(target, cancellationToken);
        }

        if (new FileInfo(temporaryPath).Length != expectedLength)
        {
            File.Delete(temporaryPath);
            throw new InvalidDataException("The chunk length is invalid.");
        }

        File.Move(temporaryPath, path, true);
    }

    public Task<Stream> OpenChunkAsync(Guid uploadId, int chunkIndex, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(File.OpenRead(Path.Combine(_paths.ChunkDir(uploadId), chunkIndex.ToString())));

    public async Task<string> AssembleAsync(UploadRecord upload, CancellationToken cancellationToken)
    {
        var finalPath = _paths.FinalFilePath(upload.Id, upload.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var temporaryPath = $"{finalPath}.tmp-{Guid.NewGuid():N}";
        await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            for (var index = 0; index < upload.TotalChunks; index++)
            {
                await using var chunk = await OpenChunkAsync(upload.Id, index, cancellationToken);
                await chunk.CopyToAsync(target, cancellationToken);
            }
        }

        File.Move(temporaryPath, finalPath, true);
        return finalPath;
    }

    public Task DeleteChunksAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var directory = _paths.ChunkDir(uploadId);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        return Task.CompletedTask;
    }
}

