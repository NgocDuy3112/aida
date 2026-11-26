using System.Reflection;
using System.Text;
using aida.Components.Pages.Chat;
using aida.Services.Ingestion;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace aida.Tests.Components;

/// <summary>
/// The upload path: a file that cannot be read is not kept as an attachment and the composer says so
/// instead of silently clearing the status.
/// </summary>
public class ChatUploadTests
{
    [Fact]
    public async Task A_readable_upload_keeps_the_chip_and_clears_the_status()
    {
        var ingestor = new RecordingIngestor(
            new NoScopeFactory(), new FixedEmbeddingGenerator(), new OcrOptions { Enabled = false },
            NullLogger<DocumentIngestor>.Instance);
        using var harness = ChatTestHarness.Render(ingestor: ingestor);
        var root = Directory.CreateTempSubdirectory("aida-upload").FullName;
        try
        {
            var environment = (FakeWebHostEnvironment)harness.Context.Services
                .GetRequiredService<IWebHostEnvironment>();
            environment.ContentRootPath = root;

            await UploadAsync(harness, new FakeBrowserFile(
                "notes.md", Encoding.UTF8.GetBytes("# Ghi chú\n\nNội dung cuộc họp.")));

            Assert.Contains("notes.md", harness.Runner.AttachedFiles);
            Assert.Null(harness.Runner.Status);
            Assert.NotEmpty(ingestor.StoredChunks);
            Assert.True(File.Exists(Path.Combine(
                root, "App_Data", "documents", ChatTestHarness.UserId, "notes.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_unreadable_upload_is_dropped_and_reported()
    {
        using var harness = ChatTestHarness.Render();
        var root = Directory.CreateTempSubdirectory("aida-upload").FullName;
        try
        {
            var environment = (FakeWebHostEnvironment)harness.Context.Services
                .GetRequiredService<IWebHostEnvironment>();
            environment.ContentRootPath = root;
            Assert.False(string.IsNullOrEmpty(harness.Runner.UserId));

            await UploadAsync(harness, new FakeBrowserFile("empty.md", []));

            Assert.Empty(harness.Runner.AttachedFiles);
            var status = Assert.IsType<string>(harness.Runner.Status);
            Assert.Contains("Không đọc được file này", status);
            Assert.Contains("ảnh scan hoặc rỗng", status);
            Assert.False(File.Exists(Path.Combine(
                root, "App_Data", "documents", ChatTestHarness.UserId, "empty.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task UploadAsync(ChatTestHarness harness, IBrowserFile file)
    {
        var upload = typeof(Chat).GetMethod(
            "UploadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return harness.Context.Renderer.Dispatcher.InvokeAsync(() =>
            (Task)upload.Invoke(harness.Page.Instance, [file])!);
    }

    private sealed class FakeBrowserFile(string name, byte[] content) : IBrowserFile
    {
        public string Name => name;

        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;

        public long Size => content.Length;

        public string ContentType => "text/markdown";

        public Stream OpenReadStream(
            long maxAllowedSize = 512 * 1024, CancellationToken cancellationToken = default) =>
            new MemoryStream(content);
    }
}
