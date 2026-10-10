using System.Text.Json;
using FluentAssertions;
using JackiVpn.Domain.Visuals;

namespace JackiVpn.UnitTests.Application;

/// <summary>The C# orb engine against the original library's golden vectors (all 9 states × 2 sizes × 4 times).</summary>
public sealed class ThinkingOrbsTests
{
    private const double Eps = 2e-4;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbs-golden-subset.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetRawText());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Frame_matches_the_reference_engine(string caseJson)
    {
        using var doc = JsonDocument.Parse(caseJson);
        var c = doc.RootElement;
        var state = Enum.Parse<OrbState>(c.GetProperty("state").GetString()!, ignoreCase: true);
        var size = c.GetProperty("size").GetInt32();
        var t = c.GetProperty("t").GetDouble();
        var preset = ThinkingOrbs.Resolve(state, size);
        var frame = new OrbFrame();

        ThinkingOrbs.Compute(preset, size, t, frame);

        var key = c.GetProperty("key").GetString();
        frame.DotCount.Should().Be(c.GetProperty("dotCount").GetInt32(), key);
        frame.LineCount.Should().Be(c.GetProperty("lineCount").GetInt32(), key);
        var flat = new List<double>();
        foreach (var d in frame.Dots)
        {
            flat.AddRange([d.X, d.Y, d.Z, d.R, d.White, d.A]);
        }

        var head = c.GetProperty("head").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var tail = c.GetProperty("tail").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        for (var i = 0; i < head.Length; i++)
        {
            flat[i].Should().BeApproximately(head[i], Eps, $"{key} head[{i}]");
        }

        for (var i = 0; i < tail.Length; i++)
        {
            flat[flat.Count - tail.Length + i].Should().BeApproximately(tail[i], Eps, $"{key} tail[{i}]");
        }

        if (state != OrbState.Solving)
        {
            // Solving assigns dots to bands by comparing coordinates with ±0.5 boundaries; a dot exactly on a
            // boundary lands by the last bit of sin/cos, which differs between JavaScript's and .NET's math libraries.
            flat.Sum().Should().BeApproximately(c.GetProperty("sum").GetDouble(), 0.05, $"{key} sum");
        }
        var lineSum = 0.0;
        foreach (var l in frame.Lines)
        {
            lineSum += l.X1 + l.Y1 + l.X2 + l.Y2 + l.White + l.A + l.W;
        }

        lineSum.Should().BeApproximately(c.GetProperty("lineSum").GetDouble(), 0.05, $"{key} lines");
    }
}
