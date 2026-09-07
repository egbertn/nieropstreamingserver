namespace Streaming.Tests;

public sealed class CopyrightCheckTests
{
    [Fact]
    public void Inspect_ReturnsNoReasonForMissingFile()
    {
        var result = CopyrightCheck.Inspect(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Null(result);
    }
}