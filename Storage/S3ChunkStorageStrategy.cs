using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using ChunkUploadExample.Models;

public sealed class S3ChunkStorageStrategy : IChunkStorage
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public S3ChunkStorageStrategy(StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.S3Bucket))
            throw new InvalidOperationException("Storage:S3Bucket is required when BlobProvider is S3.");

        var config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(options.S3Region) };
        if (!string.IsNullOrWhiteSpace(options.S3ServiceUrl))
        {
            config.ServiceURL = options.S3ServiceUrl;
            config.ForcePathStyle = true;
        }

        _client = string.IsNullOrWhiteSpace(options.S3AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(options.S3AccessKey, options.S3SecretKey, config);
        _bucket = options.S3Bucket;
    }

    public string Strategy => "S3";

    public async Task WriteChunkAsync(Guid uploadId, int chunkIndex, Stream content, long expectedLength, CancellationToken cancellationToken)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = ChunkName(uploadId, chunkIndex),
            InputStream = content,
            AutoCloseStream = false,
            AutoResetStreamPosition = false
        }, cancellationToken);
    }

    public async Task<Stream> OpenChunkAsync(Guid uploadId, int chunkIndex, CancellationToken cancellationToken)
    {
        var response = await _client.GetObjectAsync(_bucket, ChunkName(uploadId, chunkIndex), cancellationToken);
        return response.ResponseStream;
    }

    public async Task<string> AssembleAsync(UploadRecord upload, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"upload-{upload.Id:N}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (var index = 0; index < upload.TotalChunks; index++)
                {
                    await using var chunk = await OpenChunkAsync(upload.Id, index, cancellationToken);
                    await chunk.CopyToAsync(target, cancellationToken);
                }
            }

            await using var source = File.OpenRead(temporaryPath);
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = FinalName(upload),
                InputStream = source,
                AutoCloseStream = false,
                AutoResetStreamPosition = false
            }, cancellationToken);

            return $"s3://{_bucket}/{FinalName(upload)}";
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task DeleteChunksAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 100000; index++)
        {
            var key = ChunkName(uploadId, index);
            var response = await _client.DeleteObjectAsync(_bucket, key, cancellationToken);
            if (response.HttpStatusCode == System.Net.HttpStatusCode.NotFound) break;
        }
    }

    private static string ChunkName(Guid id, int index) => $"uploads/{id}/chunks/{index}";
    private static string FinalName(UploadRecord upload) => $"uploads/{upload.Id}/final/{upload.FileName}";
}
