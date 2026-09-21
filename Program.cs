using ChunkUploadExample.Models;
using ChunkUploadExampleApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

BuilderExtensions.ConfigureMetadataProviders(builder.Services, builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseHttpsRedirection();

// --------------------------------------------------------->

app.MapPost("/uploads/init", async (HttpRequest httpRequest, InitUploadRequest req, IUploadStore store, CancellationToken cancellationToken) =>
{
    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].ToString().Trim();
    
    if (idempotencyKey.Length is < 1 or > 200)
        return Results.BadRequest(new { error = "The Idempotency-Key header is required and must be between 1 and 200 characters." });

    if (req.TotalBytes <= 0 || string.IsNullOrWhiteSpace(req.FileName) ||
        Path.GetFileName(req.FileName) != req.FileName ||
        req.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        return Results.BadRequest(new { error = "FileName and TotalBytes are invalid." });

    var existing = await store.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
    
    if (existing is not null)
    {
        if (existing.FileName != req.FileName || existing.TotalBytes != req.TotalBytes)
            return Results.Conflict(new { error = "The Idempotency-Key was already used with different upload parameters." });

        return Results.Ok(new { uploadId = existing.Id, chunkSize = existing.ChunkSize, totalChunks = existing.TotalChunks });
    }

    /// --------------------------------------------------------->
    /// Upload record creation
    /// 
    
    var upload = new UploadRecord
    {
        Id = Guid.NewGuid(),
        IdempotencyKey = idempotencyKey,
        FileName = req.FileName,
        TotalBytes = req.TotalBytes,
        ChunkSize = 5 * 1024 * 1024,
        TotalChunks = (int)Math.Ceiling(req.TotalBytes / (double)(5 * 1024 * 1024)),
        Status = UploadStatus.Initiated,
        CreatedAt = DateTimeOffset.UtcNow
    };

    if (!await store.TryCreateAsync(upload, cancellationToken))
    {
        var concurrent = await store.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        return concurrent is null
            ? Results.Conflict(new { error = "The upload is being initialized concurrently." })
            : Results.Ok(new { uploadId = concurrent.Id, chunkSize = concurrent.ChunkSize, totalChunks = concurrent.TotalChunks });
    }

    return Results.Created($"/uploads/{upload.Id}/status", new { uploadId = upload.Id, chunkSize = upload.ChunkSize, totalChunks = upload.TotalChunks });
});

app.MapPut("/uploads/{id:guid}/chunks/{chunkIndex:int}", async (
    Guid id, int chunkIndex, HttpRequest request, IUploadStore store, IChunkStorage chunks, CancellationToken cancellationToken) =>
{
    var upload = await store.GetAsync(id, cancellationToken);
    if (upload is null) return Results.NotFound();
    if (upload.Status is UploadStatus.Completed or UploadStatus.Cancelled) return Results.Conflict("Upload already finalized.");
    if (chunkIndex < 0 || chunkIndex >= upload.TotalChunks) return Results.BadRequest("Invalid chunk index.");

    var expectedBytes = Math.Min(upload.ChunkSize, upload.TotalBytes - (long)chunkIndex * upload.ChunkSize);
    if (request.ContentLength.HasValue && request.ContentLength.Value != expectedBytes)
        return Results.BadRequest(new { error = "Invalid chunk size.", expectedBytes, bytesReceived = request.ContentLength.Value });

    var owner = Guid.NewGuid().ToString("N");
    if (!await store.TryAcquireLockAsync(id, owner, TimeSpan.FromMinutes(5), cancellationToken))
        return Results.Conflict("Another operation is in progress for this upload.");

    /// --------------------------------------------------------->
    /// Upload chunk by index
    /// 

    try
    {
        upload = await store.GetAsync(id, cancellationToken);
        if (upload is null) return Results.NotFound();
        if (upload.Status is UploadStatus.Completed or UploadStatus.Cancelled)
            return Results.Conflict("Upload already finalized.");

        await chunks.WriteChunkAsync(id, chunkIndex, request.Body, expectedBytes, cancellationToken);
        await store.MarkChunkReceivedAsync(id, chunkIndex, request.ContentLength ?? expectedBytes, cancellationToken);
        var updated = await store.GetAsync(id, cancellationToken)!;

        return Results.Ok(new
        {
            chunkIndex,
            bytesUploaded = updated!.BytesUploaded,
            chunksReceived = updated.ChunksReceived.Count,
            totalChunks = updated.TotalChunks,
            status = updated.Status.ToString()
        });
    }
    finally
    {
        await store.ReleaseLockAsync(id, owner, cancellationToken);
    }
});

app.MapGet("/uploads/{id:guid}/status", async (Guid id, IUploadStore store, CancellationToken cancellationToken) =>
{
    var upload = await store.GetAsync(id, cancellationToken);
    if (upload is null) return Results.NotFound();

    return Results.Ok(new
    {
        upload.Id,
        upload.FileName,
        upload.TotalChunks,
        receivedChunks = upload.ChunksReceived.OrderBy(c => c).ToArray(),
        missingChunks = Enumerable.Range(0, upload.TotalChunks).Except(upload.ChunksReceived).ToArray(),
        progressPercent = Math.Round(upload.ChunksReceived.Count * 100.0 / upload.TotalChunks, 1),
        currentStatus = upload.Status.ToString(),
        createdAt = upload.CreatedAt,
        finishedAt = upload.FinishedAt
    });
});

app.MapPost("/uploads/{id:guid}/complete", async (Guid id, IUploadStore store, IChunkStorage chunks, CancellationToken cancellationToken) =>
{
    var upload = await store.GetAsync(id, cancellationToken);
    if (upload is null) return Results.NotFound();
    if (upload.Status == UploadStatus.Completed)
        return Results.Ok(new { status = "Completed", filePath = upload.FinalFilePath });

    var owner = Guid.NewGuid().ToString("N");
    if (!await store.TryAcquireLockAsync(id, owner, TimeSpan.FromMinutes(30), cancellationToken))
        return Results.Conflict("Another operation is in progress for this upload.");

    /// --------------------------------------------------------->
    /// Complete upload
    /// 

    try
    {
        upload = await store.GetAsync(id, cancellationToken);
        if (upload is null) return Results.NotFound();
        if (upload.Status == UploadStatus.Completed)
            return Results.Ok(new { status = "Completed", filePath = upload.FinalFilePath });

        if (upload.ChunksReceived.Count != upload.TotalChunks)
            return Results.BadRequest(new { error = "Not all chunks received.", missing = Enumerable.Range(0, upload.TotalChunks).Except(upload.ChunksReceived) });

        var finalPath = await chunks.AssembleAsync(upload, cancellationToken);
        await store.MarkCompletedAsync(id, finalPath, upload.TotalBytes, UploadCompletionResult.Success, cancellationToken);
        await chunks.DeleteChunksAsync(id, cancellationToken);
        return Results.Ok(new { status = "Completed", filePath = finalPath });
    }
    finally
    {
        await store.ReleaseLockAsync(id, owner, cancellationToken);
    }
});

app.Run();
