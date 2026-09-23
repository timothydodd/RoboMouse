using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using Xunit;

namespace RoboMouse.Core.Tests;

public class FileOfferValidatorTests
{
    private static FileOfferEntry File(string path, long size = 10) => new() { RelativePath = path, Size = size };
    private static FileOfferEntry Dir(string path) => new() { RelativePath = path, IsDirectory = true };

    [Theory]
    [InlineData("..\\x")]
    [InlineData("photos\\..\\..\\x")]
    [InlineData(".\\x")]
    [InlineData("C:\\x")]
    [InlineData("C:x")]
    [InlineData("\\\\srv\\share\\x")]
    [InlineData("\\x")]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("photos\\LPT1.jpg")]
    [InlineData("a/b")]
    [InlineData("a\\\\b")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("what?")]
    [InlineData("tab\there")]
    [InlineData("")]
    public void UnsafeNames_AreRejected(string path)
    {
        Assert.NotNull(FileOfferValidator.CheckPath(path));
        Assert.NotNull(FileOfferValidator.Validate(new[] { File(path) }));
    }

    [Fact]
    public void TooLongName_IsRejected()
    {
        Assert.NotNull(FileOfferValidator.CheckPath(new string('a', 260)));
        Assert.Null(FileOfferValidator.CheckPath(new string('a', 259)));
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("CONTRACT.pdf")]
    [InlineData("com10.txt")]
    [InlineData("..hidden")]
    [InlineData("a.b.c")]
    [InlineData("naïve ☃.txt")]
    public void OrdinaryNames_AreAccepted(string path) =>
        Assert.Null(FileOfferValidator.CheckPath(path));

    [Fact]
    public void ParentsBeforeChildren_IsAccepted()
    {
        Assert.Null(FileOfferValidator.Validate(new[]
        {
            Dir("photos"),
            File("photos\\a.jpg"),
            Dir("photos\\2026"),
            File("photos\\2026\\b.jpg"),
            File("notes.txt")
        }));
    }

    [Fact]
    public void ChildBeforeParent_IsRejected()
    {
        Assert.NotNull(FileOfferValidator.Validate(new[] { File("photos\\a.jpg"), Dir("photos") }));
        Assert.NotNull(FileOfferValidator.Validate(new[] { File("photos\\a.jpg") }));
    }

    [Fact]
    public void OneBadEntry_RejectsTheWholeOffer()
    {
        Assert.NotNull(FileOfferValidator.Validate(new[] { File("fine.txt"), File("..\\evil.txt") }));
    }

    [Fact]
    public void NegativeSize_IsRejected() =>
        Assert.NotNull(FileOfferValidator.Validate(new[] { File("x", -1) }));

    [Fact]
    public void TooManyEntries_IsRejected()
    {
        var entries = Enumerable.Range(0, FileOfferValidator.MaxEntries + 1).Select(i => File($"f{i}")).ToList();
        Assert.NotNull(FileOfferValidator.Validate(entries));
    }

    [Fact]
    public void EmptyOffer_IsRejected() =>
        Assert.NotNull(FileOfferValidator.Validate(Array.Empty<FileOfferEntry>()));
}
