namespace Readability.Tests;

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brackets;

[TestClass]
public class SampleTests
{
    private sealed class DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        private const DateTimeStyles ParsingStyle =
            DateTimeStyles.AllowLeadingWhite | DateTimeStyles.AllowTrailingWhite | DateTimeStyles.AssumeUniversal;

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return DateTimeOffset.Parse(reader.GetString() ?? string.Empty, CultureInfo.InvariantCulture, ParsingStyle);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            throw new NotImplementedException();
        }
    };

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new DateTimeOffsetConverter(),
        }
    };

    private static readonly Uri PageUri = new("http://fakehost/test/page.html");

    [DataTestMethod]
    [DataRow("001"), DataRow("002"), DataRow("003-metadata-preferred"), DataRow("004-metadata-space-separated-properties"),
    DataRow("aclu"), DataRow("aktualne"), DataRow("archive-of-our-own"), DataRow("ars-1"), DataRow("base-url"), DataRow("base-url-base-element"),
    DataRow("base-url-base-element-relative"), DataRow("basic-tags-cleaning"), DataRow("bbc-1"), DataRow("blogger"), DataRow("breitbart"),
    DataRow("bug-1255978"), DataRow("buzzfeed-1"), DataRow("citylab-1"), DataRow("clean-links"), DataRow("cnet"), DataRow("cnet-svg-classes"),
    DataRow("cnn"), DataRow("comment-inside-script-parsing"), DataRow("daringfireball-1"), DataRow("data-url-image"), DataRow("dev418"),
    DataRow("dropbox-blog"), DataRow("ebb-org"), DataRow("ehow-1"), DataRow("ehow-2"), DataRow("embedded-videos"), DataRow("engadget"),
    DataRow("firefox-nightly-blog"), DataRow("folha"), DataRow("gmw"), DataRow("google-sre-book-1"), DataRow("guardian-1"), DataRow("heise"),
    DataRow("herald-sun-1"), DataRow("hidden-nodes"), DataRow("hukumusume"), DataRow("iab-1"), DataRow("ietf-1"), DataRow("js-link-replacement"),
    DataRow("keep-images"), DataRow("keep-tabular-data"), DataRow("la-nacion"), DataRow("lazy-image-1"), DataRow("lazy-image-2"), DataRow("lazy-image-3"),
    DataRow("lemonde-1"), DataRow("liberation-1"), DataRow("lifehacker-post-comment-load"), DataRow("lifehacker-working"), DataRow("links-in-tables"),
    DataRow("lwn-1"), DataRow("medicalnewstoday"), DataRow("medium-1"), DataRow("medium-2"), DataRow("medium-3"), DataRow("mercurial"),
    DataRow("metadata-content-missing"), DataRow("missing-paragraphs"), DataRow("mozilla-1"), DataRow("mozilla-2"), DataRow("msn"),
    DataRow("normalize-spaces"), DataRow("nytimes-1"), DataRow("nytimes-2"), DataRow("nytimes-3"), DataRow("nytimes-4"), DataRow("nytimes-5"),
    DataRow("pixnet"), DataRow("qq"), DataRow("quanta-1"), DataRow("remove-aria-hidden"), DataRow("remove-extra-brs"), DataRow("remove-extra-paragraphs"),
    DataRow("remove-script-tags"), DataRow("reordering-paragraphs"), DataRow("replace-brs"), DataRow("replace-font-tags"), DataRow("rtl-1"),
    DataRow("rtl-2"), DataRow("rtl-3"), DataRow("rtl-4"), DataRow("salon-1"), DataRow("seattletimes-1"), DataRow("simplyfound-1"),
    DataRow("social-buttons"), DataRow("style-tags-removal"), DataRow("svg-parsing"), DataRow("table-style-attributes"), DataRow("telegraph"),
    DataRow("theverge"), DataRow("title-and-h1-discrepancy"), DataRow("tmz-1"), DataRow("toc-missing"), DataRow("topicseed-1"), DataRow("tumblr"),
    DataRow("v8-blog"), DataRow("videos-1"), DataRow("videos-2"), DataRow("visibility-hidden"), DataRow("wapo-1"), DataRow("wapo-2"),
    DataRow("webmd-1"), DataRow("webmd-2"), DataRow("wikia"), DataRow("wikipedia"), DataRow("wikipedia-2"), DataRow("wikipedia-3"),
    DataRow("wordpress"), DataRow("yahoo-1"), DataRow("yahoo-2"), DataRow("yahoo-3"), DataRow("yahoo-4"), DataRow("youth")]
    public async Task Parse_SamplePage_AsExpected(string directory)
    {
        var path = Path.GetFullPath(Path.Combine(@"..\..\..\test-pages\", directory));
        Assert.IsTrue(Path.Exists(path), $"'{path}' doesn't exist");

        var sourceFileName = Path.Combine(path, "source.html");
        await using var sourceStream = new FileStream(sourceFileName, FileMode.Open, FileAccess.Read);
        var sourceDocument = await Document.Html.ParseAsync(sourceStream, default);
        var reader = new DocumentReader(sourceDocument, PageUri);
        var parsed = reader.Parse();
        Assert.IsNotNull(parsed);

        var metadataFileName = Path.Combine(path, "expected-metadata.json");
        await using var metadataStream = new FileStream(metadataFileName, FileMode.Open, FileAccess.Read);
        var expectedMetadata = await JsonSerializer.DeserializeAsync<ArticleInfo>(metadataStream, jsonOptions);
        Assert.IsNotNull(expectedMetadata);
        var expectedFileName = Path.Combine(path, "expected.html");
        await using var expectedStream = new FileStream(expectedFileName, FileMode.Open, FileAccess.Read);
        var expectedDocument = await Document.Html.ParseAsync(expectedStream, default);

        //var actualFileName = Path.Combine(path, "actual.html");
        //await File.WriteAllTextAsync(actualFileName, parsed.Content.ToText());

        AssertAreEqual(expectedMetadata, parsed);

        var expectedContent = GetNestedRoot(expectedDocument.First<ParentTag>());
        var actualContent = GetNestedRoot(parsed.Content);
        AssertAreEqual(expectedContent, actualContent);
    }

    private static ParentTag GetNestedRoot(ParentTag root)
    {
        var tag = root;
        while (tag.HasOneChild && tag.First() is ParentTag nested)
        {
            tag = nested;
        }
        return tag;
    }

    private static void AssertAreEqual(ArticleInfo expected, ArticleInfo actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);

        Assert.AreEqual(expected.Title?.ToTrimString(), actual.Title, "Title");
        if (expected.Byline is not null)
        {
            Assert.AreEqual(expected.Byline.ToTrimString(), actual.Byline, "Byline");
        }
        Assert.AreEqual(expected.Excerpt?.ToTrimString(), actual.Excerpt, "Excerpt");
        Assert.AreEqual(expected.SiteName, actual.SiteName, "SiteName");
        if (expected.Dir is not null)
        {
            //Assert.AreEqual(expected.Dir, actual.Dir, "Dir");
        }
        if (expected.Language is not null)
        {
            Assert.AreEqual(expected.Language, actual.Language, "Language");
        }
        if (expected.Published is not null)
        {
            // we don't compare the time-offset component here
            Assert.AreEqual(expected.Published?.DateTime, actual.Published?.DateTime, "PublishedTime");
        }
    }

    private static void AssertAreEqual(IEnumerable<Element>? expectedElements, IEnumerable<Element>? actualElements)
    {
        Assert.IsNotNull(expectedElements);
        Assert.IsNotNull(actualElements);

        var expectedEnumerator = expectedElements.GetEnumerator();
        var actualEnumerator = actualElements.GetEnumerator();

        while (actualEnumerator.MoveNext() && expectedEnumerator.MoveNext())
        {
            var expected = expectedEnumerator.Current;
            var actual = actualEnumerator.Current;

            var elemStr = $"{expected.GetType().Name}: {expected.Offset}";

            Assert.AreNotSame(expected, actual, elemStr);

            if (expected is CharacterData expectedChars && actual is CharacterData actualChars)
            {
                //Assert.AreEqual(expectedChars.Length, actualChars.Length, elemStr);
                Assert.AreEqual(expectedChars.Data.ToTrimString(), actualChars.Data.ToTrimString(), elemStr);
            }
            else if (expected is Tag expectedTag && actual is Tag actualTag)
            {
                Assert.AreEqual(expectedTag.Name, actualTag.Name, elemStr);
                AssertAreEqual(expectedTag.EnumerateAttributes().OrderBy(at => at.Name), actualTag.EnumerateAttributes().OrderBy(at => at.Name));

                if (expectedTag is ParentTag expectedParent && actualTag is ParentTag actualParent)
                {
                    AssertAreEqual(expectedParent, actualParent);
                }
                else
                {
                    Assert.IsInstanceOfType(actualTag, expectedTag.GetType(),  "Incompatible tag types");
                }
            }
            else if (expected is Attr expectedAttr && actual is Attr actualAttr)
            {
                Assert.AreEqual(expectedAttr.HasValue, actualAttr.HasValue, elemStr);
                Assert.AreEqual(expectedAttr.Name, actualAttr.Name, elemStr);
                Assert.AreEqual(WebUtility.HtmlDecode(expectedAttr.ToString()), 
                    WebUtility.HtmlDecode(actualAttr.ToString()), elemStr);
            }
            else
            {
                Assert.IsInstanceOfType(actual, expected.GetType(), "Incompatible element types");
            }
        }

        Assert.IsFalse(expectedEnumerator.MoveNext());
        Assert.IsFalse(actualEnumerator.MoveNext());
    }
}