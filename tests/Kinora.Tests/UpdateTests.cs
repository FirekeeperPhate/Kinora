using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kinora.Core.Updates;

namespace Kinora.Tests;

public class UpdateTests
{
    private static readonly byte[] Package = Encoding.UTF8.GetBytes("pretend installer");
    private static string Sha => Convert.ToHexString(SHA256.HashData(Package)).ToLowerInvariant();

    private static string Release(string tag, bool draft = false, bool pre = false, string host = "github.com", long? size = null, string? digest = null) => $$"""
        {
          "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(pre ? "true" : "false")}},
          "html_url": "https://github.com/FirekeeperPhate/Kinora/releases/tag/{{tag}}",
          "body": "What's new",
          "assets": [
            { "name": "Kinora-{{tag.TrimStart('v')}}-linux-x64.tar.gz", "size": 10,
              "browser_download_url": "https://github.com/FirekeeperPhate/Kinora/releases/download/{{tag}}/linux.tar.gz" },
            { "name": "Kinora-Setup-{{tag.TrimStart('v')}}.exe", "size": {{size ?? Package.Length}},
              "digest": "{{digest ?? "sha256:" + Sha}}",
              "browser_download_url": "https://{{host}}/FirekeeperPhate/Kinora/releases/download/{{tag}}/Kinora-Setup.exe" }
          ]
        }
        """;

    private static UpdateInfo? Parse(string json, string current = "0.1.0") =>
        UpdateService.ParseRelease(JsonDocument.Parse(json).RootElement, Version.Parse(current), v => $"Kinora-Setup-{v}.exe");

    [Fact]
    public void A_newer_release_with_this_computers_package()
    {
        var update = Parse(Release("v0.2.0"));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 0), update.Version);
        Assert.Equal("Kinora-Setup-0.2.0.exe", update.AssetName);
        Assert.Equal(Sha, update.Sha256);
        Assert.Equal("What's new", update.Notes);
    }

    [Theory]
    [InlineData("v0.1.0")]          // the same version
    [InlineData("v0.0.9")]          // older
    public void Not_newer_is_no_update(string tag) => Assert.Null(Parse(Release(tag)));

    [Fact]
    public void Drafts_prereleases_and_foreign_hosts_are_ignored()
    {
        Assert.Null(Parse(Release("v0.2.0", draft: true)));
        Assert.Null(Parse(Release("v0.2.0", pre: true)));
        Assert.Null(Parse(Release("v0.2.0", host: "evil.example.com")));
        Assert.Null(Parse(Release("v0.2.0", size: 0)));
    }

    private sealed class FakeGitHub(string release, byte[] file, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(status) { Content = new StringContent(release, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A_private_repository_is_simply_no_update()
    {
        var service = new UpdateService(new Version(0, 1, 0), new FakeGitHub("{\"message\":\"Not Found\"}", [], HttpStatusCode.NotFound));
        Assert.Null(await service.CheckAsync());
    }

    [Fact]
    public async Task The_download_is_checked_against_size_and_checksum()
    {
        var update = Parse(Release("v0.2.0"))!;
        var good = new UpdateService(new Version(0, 1, 0), new FakeGitHub("", Package));
        var path = await good.DownloadAsync(update, null);
        Assert.Equal(Package, await File.ReadAllBytesAsync(path));
        File.Delete(path);

        var tampered = new UpdateService(new Version(0, 1, 0), new FakeGitHub("", Encoding.UTF8.GetBytes("pretend installEr")));
        await Assert.ThrowsAsync<InvalidDataException>(() => tampered.DownloadAsync(update, null));

        var shorter = new UpdateService(new Version(0, 1, 0), new FakeGitHub("", Package[..5]));
        await Assert.ThrowsAsync<InvalidDataException>(() => shorter.DownloadAsync(update, null));
    }
}
