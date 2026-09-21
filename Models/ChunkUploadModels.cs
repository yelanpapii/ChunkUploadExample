using System.Text.Json.Serialization;

namespace ChunkUploadExample.Models
{
    public record InitUploadRequest(string FileName, long TotalBytes);

    public enum UploadStatus { Initiated, InProgress, Completed, Failed, Cancelled }
    public enum UploadCompletionResult { Unknown, Success, Failed, Cancelled }

    public class UploadRecord
    {
        public Guid Id { get; init; }
        public string IdempotencyKey { get; init; } = default!;
        public string FileName { get; init; } = default!;
        public long TotalBytes { get; init; }
        public int ChunkSize { get; init; }
        public int TotalChunks { get; init; }
        public UploadStatus Status { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset FinishedAt { get; set; }
        public string? FinalFilePath { get; set; }
        public UploadCompletionResult CompletionResult { get; set; } = UploadCompletionResult.Unknown;
        public HashSet<int> ChunksReceived { get; } = new();
        public long BytesUploaded { get; set; }
        [JsonIgnore]
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
