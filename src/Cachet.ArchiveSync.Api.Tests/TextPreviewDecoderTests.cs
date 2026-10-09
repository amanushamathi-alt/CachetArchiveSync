using System.Text;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-040 §13 — <see cref="TextPreviewDecoder"/> and the TXT <c>InlineText</c> path of
/// <see cref="Services.FileContentService.GetViewAsync"/>: SC-3 (encoding detection → UTF-8 with
/// <c>X-Cachet-Source-Encoding</c>) and SC-4 (truncation at <c>TextMaxBytes</c> on a character
/// boundary with <c>X-Cachet-Preview-Truncated: true</c>).
/// </summary>
public sealed class TextPreviewDecoderTests
{
    private const string Sample = "Café — naïve 日本 😀 end";

    public TextPreviewDecoderTests()
    {
        // Api's Program.cs registers this once at startup (§8.1 step 3); unit tests register it here.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static byte[] Encode(string name, string text) => name switch
    {
        "utf-8-bom" => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(),
        "utf-8" => Encoding.UTF8.GetBytes(text),
        "utf-16le-bom" => new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray(),
        "utf-16be-bom" => new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(),
        "windows-1252" => Encoding.GetEncoding(1252).GetBytes(text),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static async Task<(FileAccessResult Result, string Body)> ViewTxtAsync(byte[] bytes, int? textMaxBytes = null)
    {
        var harness = new FileContentServiceHarness();
        if (textMaxBytes is not null)
        {
            harness.Options.TextMaxBytes = textMaxBytes.Value;
        }

        harness.Seed(FileViewerTestFixtures.Record(fileIndexId: 800, extension: "txt"), bytes);
        var result = await harness.Build().GetViewAsync(800, "KeywordSearch", 1, CancellationToken.None);
        var body = result.Content is null ? string.Empty : Encoding.UTF8.GetString(await FileContentServiceHarness.ReadAllAsync(result.Content));
        return (result, body);
    }

    // -----------------------------------------------------------------------------------------
    // SC-3
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("utf-8-bom", Sample, "utf-8")]
    [InlineData("utf-8", Sample, "utf-8")]
    [InlineData("utf-16le-bom", Sample, "utf-16le")]
    [InlineData("utf-16be-bom", Sample, "utf-16be")]
    [InlineData("windows-1252", "Café naïve – “quoted” €5", "windows-1252")]
    public async Task Test_SC3_TextViewDetectsEncodingAndReturnsUtf8(string encodingName, string text, string expectedSourceEncoding)
    {
        var bytes = Encode(encodingName, text);

        // Decoder directly.
        var (decoded, sourceEncoding) = TextPreviewDecoder.Decode(bytes, truncatedAtCap: false);
        decoded.Should().Be(text);
        sourceEncoding.Should().Be(expectedSourceEncoding);
        decoded.Should().NotContain("�", "a failed strict decode always falls back to Windows-1252 (§8.1)");

        // Through /view: UTF-8 text/plain, X-Cachet-Source-Encoding set, not truncated.
        var (result, body) = await ViewTxtAsync(bytes);
        result.Outcome.Should().Be(FileContentOutcome.Success);
        result.ContentType.Should().Be("text/plain; charset=utf-8");
        result.ViewMode.Should().Be(FileViewModes.InlineText);
        result.SourceEncoding.Should().Be(expectedSourceEncoding);
        result.PreviewTruncated.Should().BeFalse();
        body.Should().Be(text);
    }

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Theory]
    // "ab€cd": € is E2 82 AC. A 4-byte cap splits € -> prefix "ab".
    [InlineData("utf-8", "ab€cd", 4, "ab", "utf-8")]
    // Cap exactly after € -> "ab€".
    [InlineData("utf-8", "ab€cd", 5, "ab€", "utf-8")]
    // BOM (3) + "a" + first 2 bytes of € -> "a".
    [InlineData("utf-8-bom", "a€b", 6, "a", "utf-8")]
    // 4-byte emoji split after 2 bytes.
    [InlineData("utf-8", "x😀y", 3, "x", "utf-8")]
    // UTF-16LE: BOM + "ab" (4) + 1 odd byte -> "ab".
    [InlineData("utf-16le-bom", "abc", 7, "ab", "utf-16le")]
    // UTF-16BE: BOM + "a" (2) + high surrogate (2) of 😀 -> "a" (surrogate pair not split).
    [InlineData("utf-16be-bom", "a😀", 6, "a", "utf-16be")]
    // UTF-16LE surrogate split likewise.
    [InlineData("utf-16le-bom", "a😀", 6, "a", "utf-16le")]
    // Windows-1252: every byte is a whole character.
    [InlineData("windows-1252", "café au lait", 6, "café a", "windows-1252")]
    public async Task Test_SC4_TextViewTruncatesAtCapOnCharacterBoundary(
        string encodingName, string text, int cap, string expectedPrefix, string expectedSourceEncoding)
    {
        var bytes = Encode(encodingName, text);
        bytes.Length.Should().BeGreaterThan(cap, "the fixture must exceed TextMaxBytes");

        var (result, body) = await ViewTxtAsync(bytes, textMaxBytes: cap);

        result.Outcome.Should().Be(FileContentOutcome.Success);
        result.PreviewTruncated.Should().BeTrue("X-Cachet-Preview-Truncated: true above TextMaxBytes");
        result.SourceEncoding.Should().Be(expectedSourceEncoding);
        body.Should().Be(expectedPrefix, "exactly the capped prefix, never a split multi-byte character");
        body.Should().NotContain("�");

        // Decoder contract directly: the same capped bytes give the same prefix.
        TextPreviewDecoder.Decode(bytes.AsSpan(0, cap), truncatedAtCap: true).Text.Should().Be(expectedPrefix);

        // Metadata reports the truncation up front for TXT.
        var harness = new FileContentServiceHarness();
        harness.Options.TextMaxBytes = cap;
        harness.Seed(FileViewerTestFixtures.Record(fileIndexId: 801, extension: "txt"), bytes);
        var metadata = await harness.Build().GetMetadataAsync(801, "KeywordSearch", CancellationToken.None);
        metadata.Metadata!.PreviewTruncated.Should().BeTrue();
    }
}
