using System.Globalization;
using Xunit;
using Xunit.Abstractions;

namespace MIQ.Tests;

public class NanProbe2Tests(ITestOutputHelper o)
{
    [Fact]
    public void ParseProbe()
    {
        foreach (var s in new[]{"nan","NaN","NAN","inf","Infinity","-inf","-Infinity"})
        {
            var ok = float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f);
            o.WriteLine($"TryParse(\"{s}\") = {ok} -> {f}");
        }
    }
}
