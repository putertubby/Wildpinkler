using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Services;

public sealed class ArchiveDownloadService
{
    private readonly HttpClient _client;

    public ArchiveDownloadService(HttpClient? client = null) => _client = client ?? new HttpClient();

    public async Task DownloadAsync(Uri source, string partialPath, string finalPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existingLength > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            existingLength = 0;
            response.Dispose();
            using var restart = await _client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await WriteAsync(restart, partialPath, finalPath, existingLength, progress, cancellationToken);
            return;
        }

        response.EnsureSuccessStatusCode();
        await WriteAsync(response, partialPath, finalPath, existingLength, progress, cancellationToken);
    }

    public async Task DownloadFromMirrorsAsync(IEnumerable<Uri> sources, string partialPath, string finalPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        foreach (var source in sources)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await DownloadAsync(source, partialPath, finalPath, progress, cancellationToken);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    lastException = exception;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }
        }

        throw lastException ?? new InvalidOperationException("No download mirrors were available.");
    }

    private static async Task WriteAsync(HttpResponseMessage response, string partialPath, string finalPath, long existingLength, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(partialPath, existingLength == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[81920];
            var total = existingLength;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                progress?.Report(total);
            }

            await output.FlushAsync(cancellationToken);
        }

        File.Move(partialPath, finalPath, true);
    }
}