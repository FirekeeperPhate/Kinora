using Kinora.Core.Scanning;

namespace Kinora.Tests;

public class NfoReaderTests
{
    private const string Emby = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <movie>
          <plot>A team heads into Afghanistan.</plot>
          <title>12 Strong</title>
          <rating>5</rating>
          <year>2018</year>
          <imdbid>tt1413492</imdbid>
          <tmdbid>429351</tmdbid>
          <premiered>2018-01-18</premiered>
          <runtime>130</runtime>
          <genre>Drama</genre>
          <genre>War</genre>
          <studio />
          <studio>Lionsgate</studio>
          <director>Nicolai Fuglsig</director>
          <actor><name>Chris Hemsworth</name><role>Captain Mitch Nelson</role></actor>
          <actor><name>Michael Shannon</name><role>Hal Spencer</role></actor>
          <fileinfo><streamdetails>
            <video><codec>h264</codec><width>1920</width><height>800</height></video>
            <audio><codec>aac</codec><channels>2</channels></audio>
          </streamdetails></fileinfo>
        </movie>
        """;

    [Fact]
    public void Reads_an_emby_nfo()
    {
        var nfo = NfoReader.Parse(Emby);
        Assert.Equal("12 Strong", nfo.Title);
        Assert.Equal(2018, nfo.Year);
        Assert.Equal(5, nfo.Rating);
        Assert.Equal("tt1413492", nfo.ImdbId);
        Assert.Equal("429351", nfo.TmdbId);
        Assert.Equal(130, nfo.Runtime);
        Assert.Equal(["Drama", "War"], nfo.Genres);
        Assert.Equal(["Lionsgate"], nfo.Studios);
        Assert.Equal(["Nicolai Fuglsig"], nfo.Directors);
        Assert.Equal(2, nfo.Actors.Count);
        Assert.Equal("Captain Mitch Nelson", nfo.Actors[0].Role);
        Assert.Equal(1920, nfo.VideoWidth);
        Assert.Equal("1080p", MovieScanner.ResolutionLabel(nfo.VideoWidth, nfo.VideoHeight));
        Assert.Equal(2, nfo.AudioChannels);
    }

    [Fact]
    public void Kodi_ratings_and_uniqueids()
    {
        var nfo = NfoReader.Parse("""
            <movie>
              <title>Heat</title>
              <ratings>
                <rating name="themoviedb"><value>7.9</value></rating>
                <rating name="imdb" default="true"><value>8.3</value></rating>
              </ratings>
              <uniqueid type="imdb" default="true">tt0113277</uniqueid>
              <uniqueid type="tmdb">949</uniqueid>
              <set><name>Michael Mann</name></set>
              <playcount>2</playcount>
            </movie>
            """);
        Assert.Equal(8.3, nfo.Rating);
        Assert.Equal("tt0113277", nfo.ImdbId);
        Assert.Equal("949", nfo.TmdbId);
        Assert.Equal("Michael Mann", nfo.Collection);
        Assert.True(nfo.Watched);
    }

    [Fact]
    public void Xml_followed_by_a_link_and_a_bare_link()
    {
        var withLink = NfoReader.Parse("<movie><title>Heat</title></movie>\nhttps://www.imdb.com/title/tt0113277/");
        Assert.Equal("Heat", withLink.Title);
        Assert.Equal("tt0113277", withLink.ImdbId);

        var bare = NfoReader.Parse("https://www.themoviedb.org/movie/949-heat");
        Assert.Null(bare.Title);
        Assert.Equal("949", bare.TmdbId);
    }

    [Fact]
    public void Broken_xml_does_not_throw()
    {
        var nfo = NfoReader.Parse("<movie><title>Heat</title><year>1995</movie> tt0113277");
        Assert.Null(nfo.Title);
        Assert.Equal("tt0113277", nfo.ImdbId);
    }

    [Fact]
    public void The_newer_nfo_wins_the_older_fills_gaps()
    {
        var newer = NfoReader.Parse("<movie><title>New title</title><genre>War</genre></movie>");
        var older = NfoReader.Parse("<movie><title>Old</title><director>Someone</director><actor><name>A</name></actor></movie>");
        newer.FillFrom(older);
        Assert.Equal("New title", newer.Title);
        Assert.Equal(["War"], newer.Genres);
        Assert.Equal(["Someone"], newer.Directors);
        Assert.Single(newer.Actors);
    }
}
