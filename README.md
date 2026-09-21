# ChunkUploadExampleApi

[English](README.md) | [Español](README.es.md)

A resumable chunked-file-upload API built with ASP.NET Core Minimal APIs on .NET 10.

The client splits a file into chunks, uploads each chunk independently, checks progress, retries failed requests safely, and finally asks the API to assemble the completed file.

> This repository is an educational example. Upload metadata is kept in memory and files are stored on the local disk. Production systems should use durable, distributed storage.

## Features

- ASP.NET Core Minimal APIs.
- Target Framework `net10.0`.
- 5 MiB upload chunks.
- Resumable uploads using received and missing chunk information.
- Idempotent chunk retries.
- Idempotent upload initialization using `Idempotency-Key`.
- Idempotent completion requests.
- HTTP streaming directly to disk without loading the entire file into memory.
- Atomic publication through temporary files and `File.Move`.
- Chunk-index and chunk-size validation.
- Per-upload concurrency coordination with `SemaphoreSlim`.
- Thread-safe state storage with `ConcurrentDictionary`.
- Cooperative cancellation through `CancellationToken`.
- OpenAPI in the Development environment.
- Nullable Reference Types enabled.
- Built-in ASP.NET Core dependency injection.

## Requirements

- .NET 10 SDK.
- Visual Studio 2026 or another .NET 10-compatible IDE.
- PowerShell, Bash, or an HTTP client such as `curl`, Postman, or REST Client.

Check the installed SDK:

```bash
dotnet --version
```

## Run the application

From the repository root:

```bash
dotnet restore
dotnet run --launch-profile https
```

Profiles configured in `Properties/launchSettings.json`:

- HTTP: `http://localhost:5138`
- HTTPS: `https://localhost:7063`

Build the project:

```bash
dotnet build
```

In Development, OpenAPI is exposed at:

```text
https://localhost:7063/openapi/v1.json
```

The exact URL can vary depending on the host configuration.

## Upload workflow

The workflow consists of four operations:

```text
1. POST /uploads/init
2. PUT  /uploads/{uploadId}/chunks/{chunkIndex}
3. GET  /uploads/{uploadId}/status
4. POST /uploads/{uploadId}/complete
```

### 1. Initialize an upload

```http
POST /uploads/init
Idempotency-Key: client-file-001
Content-Type: application/json

{
  "fileName": "video.mp4",
  "totalBytes": 12582912
}
```

Response for a new upload:

```json
{
  "uploadId": "2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2",
  "chunkSize": 5242880,
  "totalChunks": 3
}
```

A new initialization returns `201 Created` and a `Location` header pointing to the status endpoint.

The `Idempotency-Key` identifies the logical creation operation. Repeating the request with the same key and the same parameters returns the same upload. Reusing the key with a different file name or size returns `409 Conflict`.

The key must contain between 1 and 200 characters.

### 2. Upload a chunk

Chunk indexes start at zero. The previous example requires chunks `0`, `1`, and `2`.

```http
PUT /uploads/2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2/chunks/0
Content-Type: application/octet-stream

<chunk 0 bytes>
```

Example using `curl`:

```bash
curl -k -X PUT \
  "https://localhost:7063/uploads/2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2/chunks/0" \
  -H "Content-Type: application/octet-stream" \
  --data-binary "@chunk-000"
```

The API validates that:

- The upload exists.
- The upload is not completed or cancelled.
- The index is within `[0, totalChunks)`.
- The chunk size matches the expected size.
- The final chunk may be smaller than 5 MiB.

Response:

```json
{
  "chunkIndex": 0,
  "bytesUploaded": 5242880,
  "chunksReceived": 1,
  "totalChunks": 3,
  "status": "InProgress"
}
```

#### Chunk retries

The operation is idempotent by the following combination:

```text
(uploadId, chunkIndex)
```

Each chunk is stored using its index as its identity. Re-uploading the same index replaces the previous file, while `HashSet<int>` prevents the chunk from being counted twice:

```csharp
upload.ChunksReceived.Add(chunkIndex);
```

If the server completed the first request but the response was lost, the client can safely resend the chunk without duplicating logical progress.

### 3. Check upload status

```http
GET /uploads/{uploadId}/status
```

Example:

```bash
curl -k \
  "https://localhost:7063/uploads/2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2/status"
```

Example response:

```json
{
  "id": "2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2",
  "fileName": "video.mp4",
  "totalChunks": 3,
  "receivedChunks": [0, 1],
  "missingChunks": [2],
  "progressPercent": 66.7,
  "currentStatus": "InProgress",
  "createdAt": "2026-01-01T12:00:00+00:00",
  "finishedAt": "0001-01-01T00:00:00+00:00"
}
```

After a network failure, the client can poll this endpoint and upload only the missing chunks.

### 4. Complete the upload

```http
POST /uploads/{uploadId}/complete
```

Example:

```bash
curl -k -X POST \
  "https://localhost:7063/uploads/2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2/complete"
```

The API:

1. Verifies that every chunk is present.
2. Creates a temporary file for the final result.
3. Copies chunks in order.
4. Atomically moves the temporary file to its final name.
5. Marks the upload as `Completed`.
6. Deletes temporary chunks.

Response:

```json
{
  "status": "Completed",
  "filePath": "C:\\Users\\...\\AppData\\Local\\Temp\\uploads\\...\\video.mp4"
}
```

If the upload is already completed, the operation returns the persisted result without assembling the file again. This makes completion safe to retry.

## Idempotency

Idempotency means that repeating an operation produces the same final effect as executing it once.

### Initialization

Initialization uses the following header:

```http
Idempotency-Key: client-file-001
```

The store maintains a concurrent index:

```csharp
ConcurrentDictionary<string, Guid> _uploadsByIdempotencyKey
```

A key is linked to one `uploadId`. Reusing it with the same parameters returns the original resource. Reusing it with different parameters is treated as a conflict.

### Chunk upload

A chunk is identified by `(uploadId, chunkIndex)`. Its content is written to the same path and its index is tracked in a `HashSet<int>`. Retries therefore do not increment `chunksReceived` or `bytesUploaded` twice.

### Completion

The model stores `FinalFilePath` and the `Completed` state. A repeated completion request returns the previous result.

### Current idempotency boundary

Idempotency is limited to the current process because the key index is in memory. When multiple application instances are running, each instance has a different index. A distributed implementation should store idempotency keys in Redis, SQL, or another persistent store with a unique constraint.

## Architecture

This project uses a small Minimal API architecture:

```text
HTTP Client
	|
	v
Program.cs
	|
	v
UploadStoreInMemory
	|
	+--> ConcurrentDictionary of uploads
	+--> ConcurrentDictionary of idempotency keys
	+--> Local temporary directory
```

### `Program.cs`

Contains ASP.NET Core configuration and endpoint definitions:

- `POST /uploads/init`
- `PUT /uploads/{id}/chunks/{chunkIndex}`
- `GET /uploads/{id}/status`
- `POST /uploads/{id}/complete`

### `ChunkUploadExample.cs`

Defines the domain model:

- `InitUploadRequest`: initial file information.
- `UploadStatus`: upload lifecycle states.
- `UploadCompletionResult`: completion result.
- `UploadRecord`: upload state and metadata.

### `IUploadStore`

Defines the storage contract so the in-memory implementation can later be replaced with Redis, SQL, Entity Framework Core, or distributed object storage.

### `UploadStoreInMemory`

The current implementation uses:

- `ConcurrentDictionary<Guid, UploadRecord>` to find uploads by ID.
- `ConcurrentDictionary<string, Guid>` to resolve idempotency keys.
- `Path.GetTempPath()` as the root for temporary files.

## .NET technical concepts

### Minimal APIs

Endpoints are registered directly on `WebApplication` with `MapPost`, `MapPut`, and `MapGet`. This reduces controller ceremony and is suitable for small APIs and specialized services.

### Dependency injection

The store is registered as a singleton:

```csharp
builder.Services.AddSingleton<UploadStoreInMemory>();
```

ASP.NET Core resolves it automatically as a handler parameter.

### `async`/`await` and streaming

The HTTP body is copied directly to a `FileStream`:

```csharp
await request.Body.CopyToAsync(fileStream, cancellationToken);
```

The entire file is not materialized as a `byte[]`, which is important for large uploads.

### `CancellationToken`

Endpoints receive the request cancellation token and pass it to copy and wait operations. If the client disconnects, the server can stop pending work.

### `IAsyncDisposable`

Streams use `await using`, ensuring that asynchronous resources are disposed correctly.

### Concurrency

`ConcurrentDictionary` protects global indexes. Each `UploadRecord` also has a `SemaphoreSlim`:

```csharp
public SemaphoreSlim Gate { get; } = new(1, 1);
```

The gate serializes chunk writes, consistent status reads, and completion for the same upload inside one process.

### Atomic writes

Data is first written to unique temporary names:

```text
chunk.tmp-{guid}
final-file.tmp-{guid}
```

`File.Move` then publishes the result. This reduces the risk that another operation reads a partially written file.

### Nullable Reference Types

The project enables:

```xml
<Nullable>enable</Nullable>
```

This makes optional values explicit, for example `UploadRecord?` when an upload does not exist.

### `DateTimeOffset`

Dates use `DateTimeOffset` to preserve UTC offset information and serialize correctly in JSON.

### Route constraints

Routes use typed constraints:

```text
{id:guid}
{chunkIndex:int}
```

ASP.NET Core rejects requests with an invalid format before invoking the handler.

### OpenAPI

Development registers OpenAPI with `MapOpenApi()`, making the API discoverable by OpenAPI-compatible tools.

### HTTP status codes

| Situation | Response |
|---|---:|
| New initialization | `201 Created` |
| Repeated initialization with the same key | `200 OK` |
| Upload not found | `404 Not Found` |
| Invalid request or chunk | `400 Bad Request` |
| Key reused with different parameters | `409 Conflict` |
| Upload already completed | `409 Conflict` for new chunks |
| Repeated completion | `200 OK` |

## Upload states

The model contains these states:

```text
Initiated  -> InProgress -> Completed
					 \-> Failed
					 \-> Cancelled
```

Current behavior:

- `Initiated`: the upload exists but no chunks have been registered.
- `InProgress`: at least one chunk has been received and the final file has not been published.
- `Completed`: the final file has been assembled and published.
- `Failed` and `Cancelled`: available in the model, but no dedicated HTTP endpoints are implemented yet.

## Project structure

```text
ChunkUploadExample/
├── ChunkUploadExample.csproj
├── ChunkUploadExample.cs
├── Program.cs
├── UploadStoreInMemory.cs
├── Interfaces/
│   └── IUploadStore.cs
├── Properties/
│   └── launchSettings.json
├── appsettings.json
├── appsettings.Development.json
├── ChunkUploadExample.http
├── README.md
└── README.es.md
```

## Local storage

Chunks are stored in a structure similar to:

```text
%TEMP%/uploads/{uploadId}/chunks/{chunkIndex}
```

The final file is stored at:

```text
%TEMP%/uploads/{uploadId}/{fileName}
```

The actual path depends on the operating system because the implementation uses `Path.GetTempPath()` and `Path.Combine()`.

## Security considerations

The example performs basic filename validation to prevent the client from selecting a path outside the upload directory:

```csharp
Path.GetFileName(req.FileName) == req.FileName
```

A production service should also add:

- Authentication and authorization.
- Ownership checks between users and uploads.
- Per-user size limits and quotas.
- Rate limiting.
- MIME type and extension validation.
- Antivirus scanning.
- SHA-256 hashes for chunks and the complete file.
- Protection against idempotency-key abuse.
- No physical server paths in public responses.
- Structured logging and correlation IDs.
- Expiration and cleanup policies for abandoned uploads.

## Known limitations

This implementation is not a production backend because:

1. In-memory state is lost when the application restarts.
2. In-memory storage is not shared between instances.
3. Local disk is unsuitable for ephemeral containers or multi-instance deployments.
4. Authentication and authorization are not implemented.
5. Abandoned uploads are not cleaned up automatically.
6. Content checksums are not implemented.
7. Internal retries and post-upload processing queues are not implemented.
8. `Failed` and `Cancelled` do not have HTTP operations yet.
9. `SemaphoreSlim` coordinates only operations inside one process.
10. There is no transaction spanning in-memory state and filesystem changes.

## Recommended production evolution

A more robust architecture could use:

```text
ASP.NET Core API
	|
	+--> SQL / EF Core: metadata and state
	+--> Redis: idempotency, locks, and temporary progress
	+--> Azure Blob Storage / S3: chunks and final object
	+--> Queue / Service Bus: antivirus, transcoding, and events
```

Recommended changes:

- Replace `UploadStoreInMemory` with a persistent implementation.
- Use Azure Blob Storage Block Blobs or S3 Multipart Upload.
- Enforce a unique constraint for `Idempotency-Key`.
- Use distributed locks where required.
- Store hashes and sizes for every chunk.
- Add expiration and cleanup for incomplete uploads.
- Publish events after the file is completed.
- Add metrics for received bytes, error rate, duration, and active uploads.
- Configure appropriate request-size limits and timeouts.
- Add unit, integration, and concurrency tests.

## License
## Redis and object-storage providers

The application separates upload metadata from binary chunk storage. Redis stores upload metadata, idempotency keys, and distributed locks. Chunks and the final object can be stored locally, in Azure Blob Storage, or in Amazon S3.

The provider is selected in `appsettings.json`:

```json
{
  "Storage": {
	"MetadataProvider": "Redis",
	"BlobProvider": "AzureBlob",
	"Redis": {
	  "RedisConnection": "localhost:6379",
	  "RedisKeyPrefix": "chunk-upload"
	},
	"AzureBlobContainer": "uploads"
  }
}
```

Available values:

| Setting | Values |
|---|---|
| `Storage:MetadataProvider` | `InMemory`, `Redis` |
| `Storage:BlobProvider` | `Local`, `AzureBlob`, `S3` |

### Azure Blob Storage

Keep the connection string outside source control. For local development, use User Secrets or an environment variable:

```bash
dotnet user-secrets init
dotnet user-secrets set "Storage:AzureBlobConnectionString" "<connection-string>"
```

Use:

```json
{
  "Storage": {
	"MetadataProvider": "Redis",
	"BlobProvider": "AzureBlob",
	"AzureBlobContainer": "uploads"
  }
}
```

### Amazon S3

The AWS SDK can use its default credential chain (environment variables, IAM role, profile, or workload identity). Explicit credentials are supported through configuration but should not be committed:

```bash
dotnet user-secrets set "Storage:S3Bucket" "my-upload-bucket"
dotnet user-secrets set "Storage:S3Region" "eu-west-1"
```

Use:

```json
{
  "Storage": {
	"MetadataProvider": "Redis",
	"BlobProvider": "S3",
	"S3Bucket": "my-upload-bucket",
	"S3Region": "eu-west-1"
  }
}
```

For S3-compatible services, set `Storage:S3ServiceUrl`. The implementation enables path-style addressing for custom endpoints.

### Redis

Run Redis locally with Docker:

```bash
docker run --name chunk-upload-redis -p 6379:6379 -d redis:7-alpine
```

Then set `MetadataProvider` to `Redis`. The Redis implementation uses `SET NX` for idempotent creation and a Lua compare-and-delete script for safe lock release. Locks have an expiration lease and should be renewed or bounded if long-running operations can exceed the configured TTL.

This project is a technical example for educational purposes. Add an appropriate license before distributing it as a product or library.
