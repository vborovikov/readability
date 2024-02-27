namespace Readability.Tests;

using System;

[TestClass]
public class DocumentUrlTests
{
    [DataTestMethod,
        DataRow("http://fakehost/",
            "foo/bar/baz.html",
            "http://fakehost/foo/bar/baz.html"),
        DataRow("http://fakehost/",
            "./foo/bar/baz.html",
            "http://fakehost/foo/bar/baz.html"),
        DataRow("http://fakehost/",
            "/foo/bar/baz.html",
            "http://fakehost/foo/bar/baz.html"),
        //DataRow("http://fakehost/",
        //    "#foo",
        //    "http://fakehost/#foo"),
        DataRow("http://fakehost/",
            "baz.html#foo",
            "http://fakehost/baz.html#foo"),
        DataRow("http://fakehost/",
            "/foo/bar/baz.html#foo",
            "http://fakehost/foo/bar/baz.html#foo"),
        DataRow("http://fakehost/",
            "http://test/foo/bar/baz.html",
            "http://test/foo/bar/baz.html"),
        DataRow("http://fakehost/",
            "https://test/foo/bar/baz.html",
            "https://test/foo/bar/baz.html"),
        DataRow("http://fakehost/",
            "foo/bar/baz.png",
            "http://fakehost/foo/bar/baz.png"),
        DataRow("http://fakehost/",
            "./foo/bar/baz.png",
            "http://fakehost/foo/bar/baz.png"),
        DataRow("http://fakehost/",
            "/foo/bar/baz.png",
            "http://fakehost/foo/bar/baz.png"),
        DataRow("http://fakehost/",
            "http://test/foo/bar/baz.png",
            "http://test/foo/bar/baz.png"),
        DataRow("http://fakehost/",
            "https://test/foo/bar/baz.png",
            "https://test/foo/bar/baz.png")]
    public void TryMakeAbsolute_DocBaseUrlRelativePageUrls_SomeMadeAbsolute(string documentUrl, string url, string expectedUrl)
    {
        var docUrl = new DocumentUrl(new Uri(documentUrl));
        var result = docUrl.TryMakeAbsolute(url, out var actualUrl);
        if (!result) actualUrl = url;
        Assert.AreEqual(expectedUrl, actualUrl);
    }

    [DataTestMethod,
        DataRow("http://fakehost/test/base/page.html",
            "foo/bar/baz.html",
            "http://fakehost/test/base/foo/bar/baz.html"),
        DataRow("http://fakehost/test/base/page.html",
            "./foo/bar/baz.html",
            "http://fakehost/test/base/foo/bar/baz.html"),
        DataRow("http://fakehost/test/base/page.html",
            "/foo/bar/baz.html",
            "http://fakehost/foo/bar/baz.html"),
        //DataRow("http://fakehost/test/base/page.html",
        //    "#foo",
        //    "http://fakehost/test/base/#foo"),
        DataRow("http://fakehost/test/base/page.html",
            "baz.html#foo",
            "http://fakehost/test/base/baz.html#foo"),
        DataRow("http://fakehost/test/base/page.html",
            "/foo/bar/baz.html#foo",
            "http://fakehost/foo/bar/baz.html#foo"),
        DataRow("http://fakehost/test/base/page.html",
            "http://test/foo/bar/baz.html",
            "http://test/foo/bar/baz.html"),
        DataRow("http://fakehost/test/base/page.html",
            "https://test/foo/bar/baz.html",
            "https://test/foo/bar/baz.html"),
        DataRow("http://fakehost/test/base/page.html",
            "foo/bar/baz.png",
            "http://fakehost/test/base/foo/bar/baz.png"),
        DataRow("http://fakehost/test/base/page.html",
            "./foo/bar/baz.png",
            "http://fakehost/test/base/foo/bar/baz.png"),
        DataRow("http://fakehost/test/base/page.html",
            "/foo/bar/baz.png",
            "http://fakehost/foo/bar/baz.png"),
        DataRow("http://fakehost/test/base/page.html",
            "http://test/foo/bar/baz.png",
            "http://test/foo/bar/baz.png"),
        DataRow("http://fakehost/test/base/page.html",
            "https://test/foo/bar/baz.png",
            "https://test/foo/bar/baz.png")]
    public void TryMakeAbsolute_DocFullUrlRelativePageUrls_SomeMadeAbsolute(string documentUrl, string url, string expectedUrl)
    {
        var docUrl = new DocumentUrl(new Uri(documentUrl));
        var result = docUrl.TryMakeAbsolute(url, out var actualUrl);
        if (!result) actualUrl = url;
        Assert.AreEqual(expectedUrl, actualUrl);
    }
}
