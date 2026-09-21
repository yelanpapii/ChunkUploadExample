public sealed class StorageOptions
{
    public string MetadataProvider { get; set; } = "InMemory";
    public string BlobProvider { get; set; } = "Local";
    public RedisStorageOptions Redis { get; set; } = new();
    public string AzureBlobConnectionString { get; set; } = string.Empty;
    public string AzureBlobContainer { get; set; } = "uploads";
    public string S3ServiceUrl { get; set; } = string.Empty;
    public string S3Region { get; set; } = "us-east-1";
    public string S3Bucket { get; set; } = string.Empty;
    public string S3AccessKey { get; set; } = string.Empty;
    public string S3SecretKey { get; set; } = string.Empty;
}