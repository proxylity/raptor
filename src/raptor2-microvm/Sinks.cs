using System.Collections.Concurrent;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Raptor2.MicroVm;

public readonly record struct SinkPart(int PartNumber, long Size, string ETag);

/// <summary>Where finished objects go. S3 in production; a directory for local runs and tests.</summary>
public interface IObjectSink
{
    Task PutObjectAsync(string key, byte[] data, int length, CancellationToken ct);
    Task<string> CreateUploadAsync(string key, CancellationToken ct);
    Task UploadPartAsync(string key, string uploadId, int partNumber, byte[] data, int length, CancellationToken ct);
    Task<IReadOnlyList<SinkPart>> ListPartsAsync(string key, string uploadId, CancellationToken ct);
    /// <summary>Returns false if the upload no longer exists (already completed or aborted).</summary>
    Task<bool> CompleteAsync(string key, string uploadId, IReadOnlyList<SinkPart> parts, CancellationToken ct);
    Task AbortAsync(string key, string uploadId, CancellationToken ct);
    Task<string?> ReadMarkerAsync(string markerKey, CancellationToken ct);
    /// <summary>Creates the marker only if it does not exist. Returns false if someone else created it first.</summary>
    Task<bool> TryCreateMarkerAsync(string markerKey, string value, CancellationToken ct);
    Task WriteMarkerAsync(string markerKey, string value, CancellationToken ct);
}

public sealed class S3ObjectSink : IObjectSink, IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;

    public S3ObjectSink(string bucket, string? region)
    {
        _bucket = bucket;
        var config = new AmazonS3Config { MaxErrorRetry = 4 };
        if (!string.IsNullOrEmpty(region)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        _s3 = new AmazonS3Client(config);
    }

    public void Dispose() => _s3.Dispose();

    public async Task PutObjectAsync(string key, byte[] data, int length, CancellationToken ct)
    {
        using var body = new MemoryStream(data, 0, length, writable: false);
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = body }, ct);
    }

    public async Task<string> CreateUploadAsync(string key, CancellationToken ct) =>
        (await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = _bucket, Key = key }, ct)).UploadId;

    public async Task UploadPartAsync(string key, string uploadId, int partNumber, byte[] data, int length, CancellationToken ct)
    {
        using var body = new MemoryStream(data, 0, length, writable: false);
        await _s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = _bucket, Key = key, UploadId = uploadId, PartNumber = partNumber,
            InputStream = body, PartSize = length,
        }, ct);
    }

    public async Task<IReadOnlyList<SinkPart>> ListPartsAsync(string key, string uploadId, CancellationToken ct)
    {
        var parts = new List<SinkPart>();
        int? marker = null;
        while (true)
        {
            ListPartsResponse page;
            try
            {
                page = await _s3.ListPartsAsync(new ListPartsRequest
                {
                    BucketName = _bucket, Key = key, UploadId = uploadId, PartNumberMarker = marker?.ToString(), MaxParts = 1000,
                }, ct);
            }
            catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload") { return parts; }
            foreach (var p in page.Parts ?? []) parts.Add(new(p.PartNumber ?? 0, p.Size ?? 0, p.ETag ?? ""));
            if (page.IsTruncated != true) return parts;
            marker = page.NextPartNumberMarker;
            if (marker is null) return parts;
        }
    }

    public async Task<bool> CompleteAsync(string key, string uploadId, IReadOnlyList<SinkPart> parts, CancellationToken ct)
    {
        try
        {
            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket, Key = key, UploadId = uploadId,
                PartETags = [.. parts.OrderBy(p => p.PartNumber).Select(p => new PartETag(p.PartNumber, p.ETag))],
            }, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload") { return false; }
    }

    public async Task AbortAsync(string key, string uploadId, CancellationToken ct)
    {
        try { await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = key, UploadId = uploadId }, ct); }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload") { }
    }

    public async Task<string?> ReadMarkerAsync(string markerKey, CancellationToken ct)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(_bucket, markerKey, ct);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "NoSuchKey" or "NotFound") { return null; }
    }

    public async Task<bool> TryCreateMarkerAsync(string markerKey, string value, CancellationToken ct)
    {
        try
        {
            await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = markerKey, ContentBody = value, IfNoneMatch = "*" }, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.PreconditionFailed
                                          || e.ErrorCode is "PreconditionFailed" or "ConditionalRequestConflict")
        {
            return false;
        }
    }

    public async Task WriteMarkerAsync(string markerKey, string value, CancellationToken ct) =>
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = markerKey, ContentBody = value }, ct);
}

/// <summary>Writes objects under a directory. Used for local development and tests; behaves like S3 multipart.</summary>
public sealed class FileObjectSink(string root) : IObjectSink
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly ConcurrentDictionary<string, object> _gates = new();

    private string PathFor(string key) => Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    private string UploadDir(string uploadId) => Path.Combine(_root, ".uploads", uploadId);

    public Task PutObjectAsync(string key, byte[] data, int length, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var f = File.Create(path);
        f.Write(data, 0, length);
        return Task.CompletedTask;
    }

    public Task<string> CreateUploadAsync(string key, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(UploadDir(id));
        return Task.FromResult(id);
    }

    public Task UploadPartAsync(string key, string uploadId, int partNumber, byte[] data, int length, CancellationToken ct)
    {
        var dir = UploadDir(uploadId);
        if (!Directory.Exists(dir)) throw new InvalidOperationException("NoSuchUpload");
        using var f = File.Create(Path.Combine(dir, $"{partNumber:D6}"));
        f.Write(data, 0, length);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SinkPart>> ListPartsAsync(string key, string uploadId, CancellationToken ct)
    {
        var dir = UploadDir(uploadId);
        IReadOnlyList<SinkPart> parts = Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir).Select(f => new SinkPart(int.Parse(Path.GetFileName(f)), new FileInfo(f).Length, "etag"))
                .OrderBy(p => p.PartNumber)]
            : [];
        return Task.FromResult(parts);
    }

    public Task<bool> CompleteAsync(string key, string uploadId, IReadOnlyList<SinkPart> parts, CancellationToken ct)
    {
        var dir = UploadDir(uploadId);
        lock (_gates.GetOrAdd(uploadId, _ => new object()))
        {
            if (!Directory.Exists(dir)) return Task.FromResult(false);
            var path = PathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = File.Create(path))
                foreach (var p in parts.OrderBy(p => p.PartNumber))
                    using (var input = File.OpenRead(Path.Combine(dir, $"{p.PartNumber:D6}"))) input.CopyTo(output);
            Directory.Delete(dir, recursive: true);
        }
        return Task.FromResult(true);
    }

    public Task AbortAsync(string key, string uploadId, CancellationToken ct)
    {
        var dir = UploadDir(uploadId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    public Task<string?> ReadMarkerAsync(string markerKey, CancellationToken ct)
    {
        var path = PathFor(markerKey);
        return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : null);
    }

    public Task<bool> TryCreateMarkerAsync(string markerKey, string value, CancellationToken ct)
    {
        var path = PathFor(markerKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write a temp file and move it into place, so a reader never sees a half-written marker.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, value);
        try
        {
            File.Move(temp, path, overwrite: false);
            return Task.FromResult(true);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            return Task.FromResult(false);
        }
    }

    public Task WriteMarkerAsync(string markerKey, string value, CancellationToken ct)
    {
        var path = PathFor(markerKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
        return Task.CompletedTask;
    }
}
