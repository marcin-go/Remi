using System.IO.Compression;
using System.Net;
using System.Text;
using Remi.Application;
using Remi.Domain;
using Remi.Infrastructure;
using Xunit;

namespace Remi.Tests;

public sealed class CustomerUrnDirectoryTests
{
    [Fact]
    public async Task Refresh_preserves_the_Gca_address_and_makes_it_searchable()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"remi-customer-urn-{Guid.NewGuid():N}");
        var indexFile = Path.Combine(temporaryDirectory, "customer-urn-directory.json");
        try
        {
            var directory = new GcaCustomerUrnDirectory(
                new HttpClient(new GcaSourceHandler(CreateOds())),
                new DiscardEvidenceArchive(),
                indexFile);

            var refresh = await directory.RefreshAsync(Guid.NewGuid());
            var entries = await directory.GetAllAsync();
            var entry = Assert.Single(entries);

            Assert.Equal(1, refresh.Status.OrganisationCount);
            Assert.Equal("10000006", entry.Urn);
            Assert.Equal("North East Scotland College", entry.OrganisationName);
            Assert.Equal("Gallowgate, ABERDEEN, AB25 1BN", entry.Address);
            Assert.Single(await directory.SearchAsync("AB25 1BN"));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static byte[] CreateOds()
    {
        const string content = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content
                xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
                xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
              <office:body>
                <office:spreadsheet>
                  <table:table table:name="URN_List">
                    <table:table-row>
                      <table:table-cell><text:p>URN</text:p></table:table-cell>
                      <table:table-cell><text:p>Organisation Name</text:p></table:table-cell>
                      <table:table-cell><text:p>Address Line 1</text:p></table:table-cell>
                      <table:table-cell><text:p>Address Line 2</text:p></table:table-cell>
                      <table:table-cell><text:p>Address Line 3</text:p></table:table-cell>
                      <table:table-cell><text:p>County</text:p></table:table-cell>
                      <table:table-cell><text:p>Country</text:p></table:table-cell>
                      <table:table-cell><text:p>Post Code</text:p></table:table-cell>
                    </table:table-row>
                    <table:table-row>
                      <table:table-cell><text:p>10000006</text:p></table:table-cell>
                      <table:table-cell><text:p>North East Scotland College</text:p></table:table-cell>
                      <table:table-cell><text:p>Gallowgate</text:p></table:table-cell>
                      <table:table-cell><text:p>ABERDEEN</text:p></table:table-cell>
                      <table:table-cell><text:p></text:p></table:table-cell>
                      <table:table-cell><text:p></text:p></table:table-cell>
                      <table:table-cell><text:p></text:p></table:table-cell>
                      <table:table-cell><text:p>AB25 1BN</text:p></table:table-cell>
                    </table:table-row>
                  </table:table>
                </office:spreadsheet>
              </office:body>
            </office:document-content>
            """;

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("content.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }

        return stream.ToArray();
    }

    private sealed class GcaSourceHandler(byte[] ods) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = request.RequestUri?.Host == "www.gov.uk"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<a href=\"https://assets.publishing.service.gov.uk/media/test/GCA_Customer_URN_List_2026-08-03.ods\">Download</a>"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ods),
                };
            return Task.FromResult(response);
        }
    }

    private sealed class DiscardEvidenceArchive : IEvidenceArchive
    {
        public Task<ArchivedEvidenceFile> ArchiveAsync(
            EvidenceArchiveRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchivedEvidenceFile(request.OriginalRelativePath, request.Content.Length, "test-hash"));

        public Task<Stream?> OpenReadAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task DeleteAsync(EvidenceRecord evidence, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
