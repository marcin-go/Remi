using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Remi.Tests;

public sealed class DesignAccessibilityTests
{
    [Theory]
    [InlineData("--color-navy", "#FFFFFF", 4.5)]
    [InlineData("--color-navy", "#F4F7F8", 4.5)]
    [InlineData("--color-teal", "#FFFFFF", 4.5)]
    [InlineData("--color-teal-hover", "#FFFFFF", 4.5)]
    [InlineData("--color-text", "#FFFFFF", 4.5)]
    [InlineData("--color-text-muted", "#F4F7F8", 4.5)]
    [InlineData("--color-focus", "#FFFFFF", 3.0)]
    public void Core_tokens_meet_required_contrast_against_their_surfaces(string token, string background, double minimumContrast)
    {
        var ratio = Contrast(Token(token), background);

        Assert.True(ratio >= minimumContrast, $"{token} has contrast {ratio:F2}:1 against {background}; expected at least {minimumContrast:F1}:1.");
    }

    [Theory]
    [InlineData("--color-success")]
    [InlineData("--color-warning")]
    [InlineData("--color-error")]
    public void Status_tokens_remain_legible_on_their_tinted_status_surfaces(string token)
    {
        var foreground = Token(token);
        var tintedSurface = Blend(foreground, "#FFFFFF", 0.11);
        var ratio = Contrast(foreground, tintedSurface);

        Assert.True(ratio >= 4.5, $"{token} has contrast {ratio:F2}:1 against its status surface; expected at least 4.5:1.");
    }

    [Fact]
    public void Interactive_controls_have_a_visible_focus_ring_and_minimum_touch_targets()
    {
        var css = File.ReadAllText(AppCssPath());

        Assert.Contains(".button:focus-visible, input:focus-visible, select:focus-visible, a:focus-visible { outline: 3px solid var(--color-focus);", css, StringComparison.Ordinal);
        Assert.Contains(".button { display: inline-flex; justify-content: center; min-height: 2.75rem;", css, StringComparison.Ordinal);
        Assert.Contains(".checkbox-cell input { width: 1.5rem; height: 1.5rem;", css, StringComparison.Ordinal);
        Assert.Contains(".quick-filter { min-height: 1.875rem;", css, StringComparison.Ordinal);
    }

    private static string Token(string token)
    {
        var match = Regex.Match(File.ReadAllText(AppCssPath()), $"{Regex.Escape(token)}:\\s*(#[0-9A-Fa-f]{{6}})");
        Assert.True(match.Success, $"Could not find {token} in app.css.");
        return match.Groups[1].Value;
    }

    private static string AppCssPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Remi.Web", "wwwroot", "app.css");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate src/Remi.Web/wwwroot/app.css from the test output directory.");
    }

    private static double Contrast(string first, string second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static string Blend(string foreground, string background, double opacity)
    {
        var foregroundChannels = Channels(foreground);
        var backgroundChannels = Channels(background);
        var blendedChannels = Enumerable.Range(0, 3).Select(index =>
        {
            var channel = (int)Math.Round((foregroundChannels[index] * opacity) + (backgroundChannels[index] * (1 - opacity)));
            return channel.ToString("X2", CultureInfo.InvariantCulture);
        });
        return $"#{string.Concat(blendedChannels)}";
    }

    private static double Luminance(string color)
    {
        var channels = Channels(color).Select(channel =>
        {
            var value = channel / 255d;
            return value <= 0.04045d ? value / 12.92d : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }).ToArray();
        return (0.2126d * channels[0]) + (0.7152d * channels[1]) + (0.0722d * channels[2]);
    }

    private static int[] Channels(string color) =>
    [
        int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
    ];
}
