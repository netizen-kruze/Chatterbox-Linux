using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Shared by the model and pack downloads: a stalled connection fails after
// IdleTimeoutMs without a byte instead of hanging forever behind a Cancel
// button, and a download is refused up front when the disk can't hold it.
public static class SttDownload
{
    public const int IdleTimeoutMs = 60_000;

    // Copies source to target while hashing, reporting cumulative bytes.
    public static async Task<long> CopyAsync(Stream source, Stream target, SHA256 sha,
        Action<long> progress, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        long received = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeoutMs);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no data received for {IdleTimeoutMs / 1000} s — check the connection and try again");
            }
            if (read <= 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha.TransformBlock(buffer, 0, read, null, 0);
            received += read;
            progress(received);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return received;
    }

    // Fetches url into partialPath, continuing a previous attempt when one
    // is there and the server honours Range (Hugging Face, nuget.org and
    // PyPI's CDN all do), hashing the whole file either way. Returns the
    // bytes now in the file; the caller checks size and hash and keeps the
    // partial file on a plain failure so the next try picks it up.
    public static async Task<long> ResumableDownloadAsync(HttpClient http, string url, string partialPath, long expectedSize,
        SHA256 sha, Action<long> progress, CancellationToken ct)
    {
        long have = 0;
        try
        {
            var fi = new FileInfo(partialPath);
            if (fi.Exists && fi.Length > 0 && fi.Length < expectedSize) have = fi.Length;
        }
        catch { }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) have = 0;

        await using var target = new FileStream(partialPath, resumed ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (resumed)
        {
            var buffer = new byte[256 * 1024];
            long hashed = 0;
            while (hashed < have)
            {
                int n = await target.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, have - hashed)), ct).ConfigureAwait(false);
                if (n <= 0) break;
                sha.TransformBlock(buffer, 0, n, null, 0);
                hashed += n;
            }
            have = hashed;
            target.SetLength(have);
            target.Position = have;
        }
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        long got = await CopyAsync(source, target, sha, n => progress(have + n), ct).ConfigureAwait(false);
        return have + got;
    }

    // Throws an IOException in plain words when the target drive lacks room.
    public static void EnsureFreeSpace(string directory, long bytesNeeded)
    {
        string root;
        long free;
        try
        {
            var full = Path.GetFullPath(directory);
            root = Path.GetPathRoot(full) ?? "";
            if (root.Length == 0) return;
            // The mount that matters is the folder's own (home is often its
            // own filesystem); DriveInfo answers for any existing directory.
            if (OperatingSystem.IsLinux()) { Directory.CreateDirectory(full); root = full; }
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return; } // unusual drive layout — let the download try
        if (free < bytesNeeded)
            throw new IOException(
                $"not enough space in {root}: needs {bytesNeeded / 1_000_000} MB free, has {free / 1_000_000} MB");
    }
}
