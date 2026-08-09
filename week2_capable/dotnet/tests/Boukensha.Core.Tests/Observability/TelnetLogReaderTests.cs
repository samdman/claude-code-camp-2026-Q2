using Boukensha.Observability;
using Xunit;

namespace Boukensha.Core.Tests.Observability;

public class TelnetLogReaderTests
{
    private const string RecvLine =
        """{"at":"2026-08-10T08:17:09.844+12:00","direction":"recv","text":"\r\nAttempting to Detect Client, Please Wait...\r\n"}""";

    private const string SendLine =
        """{"at":"2026-08-10T08:17:11.245+12:00","direction":"send","text":"dummy"}""";

    [Fact]
    public void ReadEntries_ParsesDirectionTextAndTimestamp()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("boukensha_telnet_reader_test").FullName, "telnet.jsonl");
        File.WriteAllLines(path, [RecvLine, SendLine]);

        var entries = new TelnetLogReader().ReadEntries(path);

        Assert.Equal(2, entries.Count);
        Assert.Equal("recv", entries[0].Direction);
        Assert.Contains("Attempting to Detect Client", entries[0].Text);
        Assert.Equal("send", entries[1].Direction);
        Assert.Equal("dummy", entries[1].Text);
    }

    [Fact]
    public void ReadEntries_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new TelnetLogReader().ReadEntries(Path.Combine(Path.GetTempPath(), "does-not-exist.jsonl")));
    }
}
