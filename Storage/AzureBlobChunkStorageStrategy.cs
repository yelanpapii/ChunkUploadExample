using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using ChunkUploadExample.Models;

public sealed class AzureBlobChunkStorageStrategy : IChunkStorage
{
    private readonly BlobContainerClient _container;

    public AzureBlobChunkStorageStrategy(StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AzureBlobConnectionString))
            throw new InvalidOperationException("Storage:AzureBlobConnectionString is required when BlobProvider is AzureBlob.");

        _container = new BlobContainerClient(options.AzureBlobConnectionString, options.AzureBlobContainer);
        _container.CreateIfNotExists();
    }

    public string Strategy => "AzureBlob";

    public async Task WriteChunkAsync(Guid uploadId, int chunkIndex, Stream content, long expectedLength, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(ChunkName(uploadId, chunkIndex));
        await blob.UploadAsync(content, overwrite: true, cancellationToken);
    }

    public Task<Stream> OpenChunkAsync(Guid uploadId, int chunkIndex, CancellationToken cancellationToken) =>
        _container.GetBlobClient(ChunkName(uploadId, chunkIndex)).OpenReadAsync(cancellationToken: cancellationToken).ContinueWith(t => (Stream)t.Result, cancellationToken);

    public async Task<string> AssembleAsync(UploadRecord upload, CancellationToken cancellationToken)
    {
        var name = FinalName(upload);
        var finalBlob = _container.GetBlockBlobClient(name);
        await using (var target = await finalBlob.OpenWriteAsync(overwrite: true, options: null, cancellationToken))
        {
            for (var index = 0; index < upload.TotalChunks; index++)
            {
                await using var chunk = await OpenChunkAsync(upload.Id, index, cancellationToken);
                await chunk.CopyToAsync(target, cancellationToken);
            }
        }

        return $"azure://{_container.Name}/{name}";
    }

    public async Task DeleteChunksAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 100000; index++)
        {
            var blob = _container.GetBlobClient(ChunkName(uploadId, index));
            if (!await blob.ExistsAsync(cancellationToken)) break;
            await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }
    }

    private static string ChunkName(Guid id, int index) => $"uploads/{id}/chunks/{index}";
    private static string FinalName(UploadRecord upload) => $"uploads/{upload.Id}/final/{upload.FileName}";
}
