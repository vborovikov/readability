namespace Readability.Tests;

[TestClass]
public class DataUrlTests
{
    [DataTestMethod]
    [DataRow("data:,A%20brief%20note", "", "")]
    [DataRow("data:text/plain;charset=iso-8859-7,%be%fg%be", "text/plain", "")]
    [DataRow("data:application/vnd-xxx-query,select_vcount,fcol_from_fieldtable/local", "application/vnd-xxx-query", "")]
    [DataRow("data:text/plain;base64,SGVsbG8sIFdvcmxkIQ==", "text/plain", "base64")]
    [DataRow("data:text/html,%3Ch1%3EHello%2C%20World%21%3C%2Fh1%3E", "text/html", "")]
    [DataRow("data:text/html,%3Cscript%3Ealert%28%27hi%27%29%3B%3C%2Fscript%3E", "text/html", "")]
    [DataRow(
        """
        data:image/gif;base64,R0lGODdhMAAwAPAAAAAAAP///ywAAAAAMAAwAAAC8IyPqcvt3wCcDkiLc7C0qwyGHhSWpjQu5yqmCYsapyuvUUlvONmOZtfzgFzByTB10QgxOR0TqBQejhRNzOfkVJ+5YiUqrXF5Y5lKh/DeuNcP5yLWGsEbtLiOSpa/TPg7JpJHxyendzWTBfX0cxOnKPjgBzi4diinWGdkF8kjdfnycQZXZeYGejmJlZeGl9i2icVqaNVailT6F5iJ90m6mvuTS4OK05M0vDk0Q4XUtwvKOzrcd3iq9uisF81M1OIcR7lEewwcLp7tuNNkM3uNna3F2JQFo97Vriy/Xl4/f1cf5VWzXyym7PHhhx4dbgYKAAA7
        """, "image/gif", "base64")]
    public void TryParse_ValidDataUrls_Parsed(string dataUrl, string mimeType, string encoding)
    {
        Assert.IsTrue(DataUrl.TryParse(dataUrl, out var result));
        Assert.AreEqual(mimeType, result.MimeType.ToString());
        Assert.AreEqual(encoding, result.Encoding.ToString());
    }

    [DataTestMethod]
    [DataRow("data:none")]
    [DataRow("http://www.example.com/")]
    public void TryParse_InvalidDataUrls_ReturnsFalse(string dataUrl)
    {
        Assert.IsFalse(DataUrl.TryParse(dataUrl, out _));
    }
}
