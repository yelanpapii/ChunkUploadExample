# ChunkUploadExampleApi

[English](README.md) | [Español](README.es.md)

API de ejemplo para realizar subidas de archivos por chunks con ASP.NET Core Minimal APIs sobre .NET 10.

El proyecto demuestra cómo implementar una subida reanudable: el cliente divide un archivo en fragmentos, los envía individualmente, consulta el progreso y finalmente solicita el ensamblado del archivo final.

> Este repositorio es un ejemplo educativo. El estado se almacena en memoria y los archivos se escriben en el disco local. Para producción se recomienda utilizar almacenamiento persistente y distribuido.

## Características

- ASP.NET Core Minimal APIs.
- Target Framework `net10.0`.
- Subida de archivos por chunks de 5 MiB.
- Reanudación mediante consulta de chunks recibidos y faltantes.
- Reintentos idempotentes de chunks.
- Idempotencia de la inicialización mediante `Idempotency-Key`.
- Finalización idempotente del upload.
- Streaming HTTP a disco sin cargar el archivo completo en memoria.
- Escrituras atómicas mediante archivos temporales y `File.Move`.
- Validación de índices y tamaños de chunks.
- Coordinación de operaciones concurrentes por upload mediante `SemaphoreSlim`.
- Almacenamiento thread-safe con `ConcurrentDictionary`.
- Cancelación cooperativa mediante `CancellationToken`.
- OpenAPI disponible en entorno de desarrollo.
- Nullable Reference Types habilitado.
- Inyección de dependencias integrada de ASP.NET Core.

## Requisitos

- .NET 10 SDK.
- Visual Studio 2026 o cualquier IDE compatible con .NET 10.
- PowerShell, Bash o una herramienta HTTP como `curl`, Postman o REST Client.

Comprobar el SDK instalado:

```bash
dotnet --version
```

## Ejecución

Desde la raíz del repositorio:

```bash
dotnet restore
dotnet run --launch-profile https
```

Perfiles disponibles en `Properties/launchSettings.json`:

- HTTP: `http://localhost:5138`
- HTTPS: `https://localhost:7063`

Para compilar:

```bash
dotnet build
```

La documentación OpenAPI se expone en desarrollo mediante:

```text
https://localhost:7063/openapi/v1.json
```

La URL exacta puede variar según la configuración del host.

## Flujo de una subida

El flujo consta de cuatro operaciones:

```text
1. POST /uploads/init
2. PUT  /uploads/{uploadId}/chunks/{chunkIndex}
3. GET  /uploads/{uploadId}/status
4. POST /uploads/{uploadId}/complete
```

### 1. Inicializar el upload

```http
POST /uploads/init
Idempotency-Key: cliente-archivo-001
Content-Type: application/json

{
  "fileName": "video.mp4",
  "totalBytes": 12582912
}
```

Respuesta de una inicialización nueva:

```json
{
  "uploadId": "2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2",
  "chunkSize": 5242880,
  "totalChunks": 3
}
```

La respuesta nueva utiliza `201 Created` y devuelve una cabecera `Location` apuntando al endpoint de estado.

La cabecera `Idempotency-Key` identifica lógicamente la operación de creación. Si el cliente repite la petición con la misma clave y los mismos parámetros, la API devuelve el mismo upload. Si reutiliza la clave con otro nombre o tamaño, responde con `409 Conflict`.

La clave debe tener entre 1 y 200 caracteres.

### 2. Subir un chunk

Los índices empiezan en cero. Para el ejemplo anterior habría que enviar los chunks `0`, `1` y `2`.

```http
PUT /uploads/2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2/chunks/0
Content-Type: application/octet-stream

<bytes del chunk 0>
```

Ejemplo con `curl`:

```bash
curl -k -X PUT \
  "https://localhost:7063/uploads/2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2/chunks/0" \
  -H "Content-Type: application/octet-stream" \
  --data-binary "@chunk-000"
```

La API valida que:

- El upload exista.
- El upload no esté finalizado ni cancelado.
- El índice esté dentro del rango `[0, totalChunks)`.
- El tamaño del chunk coincida con el tamaño esperado.
- El último chunk pueda ser menor que 5 MiB.

Respuesta:

```json
{
  "chunkIndex": 0,
  "bytesUploaded": 5242880,
  "chunksReceived": 1,
  "totalChunks": 3,
  "status": "InProgress"
}
```

#### Reintentos de chunks

La operación es idempotente por la combinación:

```text
(uploadId, chunkIndex)
```

Cada chunk se guarda usando su índice como identidad. Reenviar el mismo índice reemplaza el archivo anterior y `HashSet<int>` evita contar el chunk dos veces:

```csharp
upload.ChunksReceived.Add(chunkIndex);
```

Si la primera petición terminó en el servidor pero la respuesta se perdió, el cliente puede reenviar el chunk sin duplicar el progreso lógico.

### 3. Consultar el estado

```http
GET /uploads/{uploadId}/status
```

Ejemplo:

```bash
curl -k \
  "https://localhost:7063/uploads/2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2/status"
```

Respuesta aproximada:

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

El cliente puede consultar este endpoint después de una desconexión para continuar únicamente con los chunks faltantes.

### 4. Completar el upload

```http
POST /uploads/{uploadId}/complete
```

Ejemplo:

```bash
curl -k -X POST \
  "https://localhost:7063/uploads/2c4c4a0e-09b9-4fd4-87d0-58c4e7f158e2/complete"
```

La API:

1. Verifica que todos los chunks estén presentes.
2. Crea un archivo temporal para el resultado final.
3. Copia los chunks en orden.
4. Mueve el temporal al nombre final mediante una operación atómica.
5. Marca el upload como `Completed`.
6. Elimina los chunks temporales.

Respuesta:

```json
{
  "status": "Completed",
  "filePath": "C:\\Users\\...\\AppData\\Local\\Temp\\uploads\\...\\video.mp4"
}
```

Si el upload ya está completado, la misma operación devuelve la información persistida sin volver a ensamblar el archivo. Esto proporciona idempotencia frente a reintentos de la operación de finalización.

## Idempotencia

La idempotencia significa que repetir una operación produce el mismo efecto final que ejecutarla una sola vez.

### Inicialización

La inicialización usa el header:

```http
Idempotency-Key: cliente-archivo-001
```

El almacén mantiene un índice concurrente:

```csharp
ConcurrentDictionary<string, Guid> _uploadsByIdempotencyKey
```

La clave se vincula a un único `uploadId`. Reutilizarla con los mismos parámetros devuelve el recurso original. Reutilizarla con parámetros diferentes se considera un conflicto.

### Subida de chunks

La identidad de un chunk es `(uploadId, chunkIndex)`. El contenido se escribe en la misma ruta y el índice se registra en un `HashSet<int>`. Por eso los reintentos no incrementan dos veces `chunksReceived` ni `bytesUploaded`.

### Finalización

El modelo conserva `FinalFilePath` y el estado `Completed`. Si una petición de finalización se repite, la API devuelve el resultado anterior.

### Límites de la idempotencia actual

La idempotencia está limitada al proceso actual porque el índice vive en memoria. Si se ejecutan varias instancias de la aplicación, cada una tendrá un índice diferente. Un sistema distribuido debería guardar las claves en Redis, SQL o una base de datos con una restricción única.

## Arquitectura

El proyecto usa una arquitectura pequeña basada en Minimal APIs:

```text
HTTP Client
	|
	v
Program.cs
	|
	v
UploadStoreInMemory
	|
	+--> ConcurrentDictionary de uploads
	+--> ConcurrentDictionary de Idempotency-Key
	+--> Directorio temporal local
```

### `Program.cs`

Contiene la configuración de ASP.NET Core y las definiciones de los endpoints:

- `POST /uploads/init`
- `PUT /uploads/{id}/chunks/{chunkIndex}`
- `GET /uploads/{id}/status`
- `POST /uploads/{id}/complete`

### `ChunkUploadExample.cs`

Define el modelo de dominio:

- `InitUploadRequest`: datos iniciales del archivo.
- `UploadStatus`: ciclo de vida del upload.
- `UploadCompletionResult`: resultado de finalización.
- `UploadRecord`: estado y metadatos del upload.

### `IUploadStore`

Define el contrato del almacenamiento para poder sustituir la implementación en memoria por Redis, SQL, Entity Framework Core o un almacenamiento distribuido.

### `UploadStoreInMemory`

Implementa el almacenamiento actual usando:

- `ConcurrentDictionary<Guid, UploadRecord>` para localizar uploads por identificador.
- `ConcurrentDictionary<string, Guid>` para resolver claves de idempotencia.
- `Path.GetTempPath()` como raíz de archivos temporales.

## Conceptos técnicos de .NET utilizados

### Minimal APIs

Los endpoints se registran directamente sobre `WebApplication` mediante `MapPost`, `MapPut` y `MapGet`. Este modelo reduce el código ceremonial de controladores y es apropiado para APIs pequeñas y servicios especializados.

### Inyección de dependencias

El almacén se registra como singleton:

```csharp
builder.Services.AddSingleton<UploadStoreInMemory>();
```

ASP.NET Core lo proporciona automáticamente como parámetro de los handlers.

### `async`/`await` y streaming

El cuerpo HTTP se copia directamente a un `FileStream`:

```csharp
await request.Body.CopyToAsync(fileStream, cancellationToken);
```

Esto evita cargar el archivo completo en un `byte[]` o en memoria administrada. Es importante para archivos grandes.

### `CancellationToken`

Los endpoints reciben el token de cancelación de la petición y lo propagan a las operaciones de copia y espera. Si el cliente desconecta, el servidor puede detener el trabajo pendiente.

### `IAsyncDisposable`

Los streams se liberan con `await using`, garantizando una liberación adecuada de recursos asíncronos.

### Concurrencia

`ConcurrentDictionary` protege las operaciones sobre los índices globales. Además, cada `UploadRecord` tiene un `SemaphoreSlim`:

```csharp
public SemaphoreSlim Gate { get; } = new(1, 1);
```

Este gate serializa la escritura de chunks, la consulta consistente del estado y la finalización de un mismo upload dentro del proceso.

### Escritura atómica

Los datos se escriben primero en nombres temporales únicos:

```text
chunk.tmp-{guid}
archivo-final.tmp-{guid}
```

Después se usa `File.Move` para publicar el resultado. Así se reduce el riesgo de que otro proceso lea un archivo parcialmente escrito.

### Nullable Reference Types

El proyecto tiene activado:

```xml
<Nullable>enable</Nullable>
```

Esto permite expresar explícitamente valores opcionales, por ejemplo `UploadRecord?` cuando un upload no existe.

### `DateTimeOffset`

Las fechas se representan con `DateTimeOffset` para conservar información de zona horaria y serializar correctamente valores UTC en JSON.

### Restricciones de routing

Las rutas declaran restricciones tipadas:

```text
{id:guid}
{chunkIndex:int}
```

ASP.NET Core descarta solicitudes que no cumplen el formato esperado antes de ejecutar el handler.

### OpenAPI

En desarrollo se registra la documentación OpenAPI mediante `MapOpenApi()`. La API puede explorarse con herramientas compatibles con OpenAPI.

### HTTP status codes

| Situación | Respuesta |
|---|---:|
| Inicialización nueva | `201 Created` |
| Inicialización repetida con misma clave | `200 OK` |
| Upload inexistente | `404 Not Found` |
| Parámetros o chunk inválidos | `400 Bad Request` |
| Clave reutilizada con otros parámetros | `409 Conflict` |
| Upload ya finalizado | `409 Conflict` para nuevos chunks |
| Finalización repetida | `200 OK` |

## Estados del upload

El modelo contiene los siguientes estados:

```text
Initiated  -> InProgress -> Completed
					 \-> Failed
					 \-> Cancelled
```

En la implementación actual:

- `Initiated`: el upload fue creado, pero todavía no hay chunks registrados.
- `InProgress`: existe al menos un chunk recibido y el archivo final aún no está publicado.
- `Completed`: el archivo final fue ensamblado y publicado.
- `Failed` y `Cancelled`: están disponibles en el modelo, pero no tienen endpoints específicos implementados.

## Estructura de archivos

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
└── ChunkUploadExample.http
```

## Almacenamiento local

Los chunks se guardan en una estructura similar a:

```text
%TEMP%/uploads/{uploadId}/chunks/{chunkIndex}
```

El resultado final se guarda en:

```text
%TEMP%/uploads/{uploadId}/{fileName}
```

La ruta depende del sistema operativo porque se obtiene mediante `Path.GetTempPath()` y `Path.Combine()`.

## Consideraciones de seguridad

El ejemplo incluye validación básica del nombre de archivo para evitar que el cliente utilice rutas externas al directorio del upload:

```csharp
Path.GetFileName(req.FileName) == req.FileName
```

En un sistema real también deberían añadirse:

- Autenticación y autorización.
- Asociación del upload con el usuario propietario.
- Límites de tamaño y cuotas por usuario.
- Rate limiting.
- Validación de tipos MIME y extensiones.
- Análisis antivirus.
- Hash SHA-256 por chunk y del archivo completo.
- Protección contra abuso de `Idempotency-Key`.
- No devolver rutas físicas del servidor al cliente.
- Registro estructurado y trazabilidad con correlation IDs.
- Política de expiración para uploads abandonados.

## Limitaciones conocidas

Esta implementación no debe considerarse un backend de producción porque:

1. El estado se pierde al reiniciar la aplicación.
2. El almacenamiento en memoria no se comparte entre instancias.
3. El disco local no es adecuado para despliegues multiinstancia o contenedores efímeros.
4. No hay autenticación ni autorización.
5. No hay limpieza automática de uploads abandonados.
6. No hay checksum para validar la integridad del contenido.
7. No hay reintentos internos ni cola de procesamiento posterior.
8. `Failed` y `Cancelled` todavía no tienen operaciones HTTP.
9. La coordinación con `SemaphoreSlim` solo funciona dentro de un proceso.
10. No existe persistencia transaccional entre el estado en memoria y los archivos.

## Evolución recomendada para producción

Una arquitectura más robusta podría utilizar:

```text
ASP.NET Core API
	|
	+--> SQL / EF Core: metadatos y estado
	+--> Redis: idempotencia, locks y progreso temporal
	+--> Azure Blob Storage / S3: chunks y archivo final
	+--> Queue / Service Bus: antivirus, transcodificación y eventos
```

Cambios recomendados:

- Sustituir `UploadStoreInMemory` por una implementación persistente.
- Utilizar Azure Blob Storage Block Blob o S3 Multipart Upload.
- Implementar una restricción única para `Idempotency-Key`.
- Usar locks distribuidos cuando sea necesario.
- Almacenar hashes y tamaños de cada chunk.
- Añadir expiración y limpieza de uploads incompletos.
- Publicar eventos después de completar el archivo.
- Añadir métricas: bytes recibidos, tasa de error, duración y uploads activos.
- Configurar límites de request y timeouts adecuados.
- Añadir pruebas unitarias, pruebas de integración y pruebas de concurrencia.

## Licencia
## Redis y proveedores de almacenamiento de objetos

La aplicación separa los metadatos del upload de los bytes de los chunks. Redis almacena los metadatos, las claves de idempotencia y los locks distribuidos. Los chunks y el objeto final pueden almacenarse localmente, en Azure Blob Storage o en Amazon S3.

El proveedor se selecciona en `appsettings.json`:

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

Valores disponibles:

| Configuración | Valores |
|---|---|
| `Storage:MetadataProvider` | `InMemory`, `Redis` |
| `Storage:BlobProvider` | `Local`, `AzureBlob`, `S3` |

### Azure Blob Storage

La cadena de conexión debe mantenerse fuera del control de código fuente. Para desarrollo local puede utilizarse User Secrets o una variable de entorno:

```bash
dotnet user-secrets init
dotnet user-secrets set "Storage:AzureBlobConnectionString" "<connection-string>"
```

Configuración:

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

El SDK de AWS puede utilizar su cadena de credenciales predeterminada: variables de entorno, rol IAM, perfil local o identidad de workload. Las credenciales explícitas se soportan mediante configuración, pero nunca deben subirse al repositorio:

```bash
dotnet user-secrets set "Storage:S3Bucket" "my-upload-bucket"
dotnet user-secrets set "Storage:S3Region" "eu-west-1"
```

Configuración:

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

Para servicios compatibles con S3 puede configurarse `Storage:S3ServiceUrl`. La implementación activa el direccionamiento path-style para endpoints personalizados.

### Redis

Ejemplo para ejecutar Redis localmente con Docker:

```bash
docker run --name chunk-upload-redis -p 6379:6379 -d redis:7-alpine
```

Después hay que establecer `MetadataProvider` en `Redis`. La implementación utiliza `SET NX` para la creación idempotente y un script Lua de comparación y borrado para liberar locks de forma segura. Los locks tienen una expiración y deben renovarse o limitarse si las operaciones pueden superar su TTL.

Este proyecto es un ejemplo técnico para fines educativos. Añade la licencia que corresponda antes de distribuirlo como producto o librería.
