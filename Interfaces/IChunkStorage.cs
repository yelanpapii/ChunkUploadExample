using ChunkUploadExample.Models;

public interface IChunkStorage
{
    string Strategy { get; }
    Task WriteChunkAsync(Guid uploadId, int chunkIndex, Stream content, long expectedLength, CancellationToken cancellationToken);
    Task<Stream> OpenChunkAsync(Guid uploadId, int chunkIndex, CancellationToken cancellationToken);
    Task<string> AssembleAsync(UploadRecord upload, CancellationToken cancellationToken);
    Task DeleteChunksAsync(Guid uploadId, CancellationToken cancellationToken);
}