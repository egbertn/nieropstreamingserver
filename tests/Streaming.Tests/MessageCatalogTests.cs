using Microsoft.AspNetCore.Http;

namespace Streaming.Tests;

public sealed class MessageCatalogTests
{
    [Theory]
    [InlineData("nl", "Track niet gevonden")]
    [InlineData("NL-be,nl;q=0.8,en;q=0.5", "Track niet gevonden")]
    [InlineData("en,nl;q=0.9", "Track not found")]
    [InlineData("fr", "Track not found")]
    [InlineData("", "Track not found")]
    public void Get_SelectsLanguageFromFirstHeaderEntry(string header, string expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.AcceptLanguage = header;

        Assert.Equal(expected, MessageCatalog.Get(request, "TRACK_NOT_FOUND"));
    }

    [Theory]
    [InlineData("nl", "Het rapport is succesvol gemaild naar test@example.com")]
    [InlineData("en", "The report was emailed to test@example.com")]
    public void Get_FormatsMessageArguments(string header, string expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.AcceptLanguage = header;

        Assert.Equal(expected, MessageCatalog.Get(request, "REPORT_EMAILED", "test@example.com"));
    }

    [Fact]
    public void Catalog_HasMatchingCodesForBothLanguages()
    {
        var dutch = MessageCatalog.Messages.Keys.Where(key => key.StartsWith("nl/")).Select(key => key[3..]).Order().ToArray();
        var english = MessageCatalog.Messages.Keys.Where(key => key.StartsWith("en/")).Select(key => key[3..]).Order().ToArray();

        Assert.NotEmpty(dutch);
        Assert.Equal(dutch, english);
        Assert.Equal(dutch.Length + english.Length, MessageCatalog.Messages.Count);
    }

    [Fact]
    public void Get_UnknownCodeFailsExplicitly()
    {
        var request = new DefaultHttpContext().Request;

        Assert.Throws<KeyNotFoundException>(() => MessageCatalog.Get(request, "UNKNOWN"));
    }
}
