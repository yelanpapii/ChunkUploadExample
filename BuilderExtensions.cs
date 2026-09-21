using StackExchange.Redis;

namespace ChunkUploadExampleApi
{
    public static class BuilderExtensions
    {
        public static IServiceCollection ConfigureMetadataProviders(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<StorageOptions>(configuration.GetSection("Storage"));

            var storageOptions = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
            storageOptions.Redis.RedisConnection = configuration.GetConnectionString("redis") ?? storageOptions.Redis.RedisConnection;
            storageOptions.AzureBlobConnectionString = configuration.GetConnectionString("uploads") ?? storageOptions.AzureBlobConnectionString;
            services.AddSingleton(storageOptions);

            if (storageOptions.MetadataProvider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(storageOptions.Redis.RedisConnection));
                services.AddSingleton<IUploadStore, RedisUploadMetadata>();
            }
            else
            {
                services.AddSingleton<InMemoryUploadMetadata>();
                services.AddSingleton<IUploadStore>(services => services.GetRequiredService<InMemoryUploadMetadata>());
            }

            if (storageOptions.BlobProvider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IChunkStorage, AzureBlobChunkStorageStrategy>();
            }
            else if (storageOptions.BlobProvider.Equals("S3", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IChunkStorage, S3ChunkStorageStrategy>();
            }
            else
            {
                services.AddSingleton<ILocalUploadPaths, LocalUploadPaths>();
                services.AddSingleton<IChunkStorage, LocalChunkStorage>();
            }

            return services;
        }

    }
}
