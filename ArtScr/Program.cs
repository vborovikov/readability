namespace ArtScr;

using Brackets;
using Readability;
using Termly;

static class Program
{
    private const int DefaultNTopCandidates = 5;

    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("HTML file path expected");
            return 1;
        }

        var htmlFile = new FileInfo(args[0]);
        if (!htmlFile.Exists)
        {
            Console.Error.WriteLine($"File '{htmlFile.FullName}' doesn't exist");
            return 2;
        }

        try
        {
            await using var htmlFileStream = htmlFile.OpenRead();
            var document = await Document.Html.ParseAsync(htmlFileStream);

            var topCandidateCount = args.Length > 1 && int.TryParse(args[1], out var num) ? num : DefaultNTopCandidates;

            var body = document
                .FirstOrDefault<ParentTag>(h => h.Name == "html")?
                .FirstOrDefault<ParentTag>(b => b.Name == "body") ??
                (IRoot)document;

            if (ArticleCandidate.TryFind(body, topCandidateCount, out var articleCandidate))
            {
                return 0;
            }
        }
        catch (Exception x)
        {
            Console.Error.WriteLine(ConsoleColor.DarkRed, x.Message);
            return 4;
        }

        return 3;
    }
}
