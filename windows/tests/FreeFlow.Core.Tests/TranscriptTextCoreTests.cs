using System.Text;
using FreeFlow.Core.Text;
using Xunit;

namespace FreeFlow.Core.Tests;

/// <summary>
/// Ported from <c>Tests/TranscriptTextCoreTests.swift</c>. All fixtures are synthetic.
/// </summary>
public class TranscriptTextCoreTests
{
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public void JsonTranscriptParsing()
    {
        Assert.Equal(
            "Synthetic transcript.",
            TranscriptionResponseParser.Parse(Bytes("""{"text":"Synthetic transcript."}""")));

        Assert.Equal("", TranscriptionResponseParser.Parse(Bytes("""{"text":""}""")));
    }

    [Fact]
    public void NonTranscriptJsonFallsBackToPlainText()
    {
        // Lines are joined with a single space without being individually trimmed.
        Assert.Equal(
            """{   "synthetic": "fallback" }""",
            TranscriptionResponseParser.Parse(Bytes("{\n  \"synthetic\": \"fallback\"\n}")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t ")]
    [InlineData("{malformed synthetic JSON")]
    public void InvalidTranscriptResponsesThrow(string body)
    {
        Assert.Throws<TranscriptionResponseParsingException>(
            () => TranscriptionResponseParser.Parse(Bytes(body)));
    }

    [Fact]
    public void UndecodablePayloadThrows()
    {
        Assert.Throws<TranscriptionResponseParsingException>(
            () => TranscriptionResponseParser.Parse(new byte[] { 0xFF, 0xFE }));
    }

    [Fact]
    public void HighConfidenceHallucinationsAreSuppressed()
    {
        var atThreshold = """{"text":"Thank you.","segments":[{"no_speech_prob":0.1}]}""";
        Assert.Equal("", TranscriptionResponseParser.Parse(Bytes(atThreshold)));

        var normalizedPhrase = """{"text":"  THANK YOU FOR WATCHING!!!  ","segments":[{"no_speech_prob":0.9}]}""";
        Assert.Equal("", TranscriptionResponseParser.Parse(Bytes(normalizedPhrase)));
    }

    [Fact]
    public void PossibleRealSpeechIsPreserved()
    {
        var lowProbability = """{"text":"Thank you.","segments":[{"no_speech_prob":0.099}]}""";
        Assert.Equal("Thank you.", TranscriptionResponseParser.Parse(Bytes(lowProbability)));

        var missingMetadata = """{"text":"Thank you."}""";
        Assert.Equal("Thank you.", TranscriptionResponseParser.Parse(Bytes(missingMetadata)));

        var missingProbability = """{"text":"Thank you.","segments":[{"synthetic":true}]}""";
        Assert.Equal("Thank you.", TranscriptionResponseParser.Parse(Bytes(missingProbability)));

        var unrelatedSpeech = """{"text":"Synthetic project update.","segments":[{"no_speech_prob":0.95}]}""";
        Assert.Equal(
            "Synthetic project update.",
            TranscriptionResponseParser.Parse(Bytes(unrelatedSpeech)));
    }

    [Fact]
    public void PostProcessedTranscriptSanitization()
    {
        Assert.Equal(
            "Synthetic output.",
            TranscriptOutputSanitizer.PostProcessedTranscript("  \"Synthetic output.\" \n"));
        Assert.Equal("", TranscriptOutputSanitizer.PostProcessedTranscript("EMPTY"));
        Assert.Equal("", TranscriptOutputSanitizer.PostProcessedTranscript("\"EMPTY\""));
        Assert.Equal("empty", TranscriptOutputSanitizer.PostProcessedTranscript("empty"));
        Assert.Equal("", TranscriptOutputSanitizer.PostProcessedTranscript("  \n "));
    }

    [Fact]
    public void ModeSpecificSanitization()
    {
        // Verbatim translation keeps EMPTY, because it may be real translated speech.
        Assert.Equal("EMPTY", TranscriptOutputSanitizer.VerbatimTranslation("  \"EMPTY\"  "));
        Assert.Equal(
            "Literal synthetic text.",
            TranscriptOutputSanitizer.VerbatimTranslation(" \"Literal synthetic text.\" "));
        // Edit Mode keeps quotes, since they may be part of the replacement text.
        Assert.Equal(
            "\"Keep command quotes\"",
            TranscriptOutputSanitizer.CommandModeTranscript("  \"Keep command quotes\" \n"));
    }

    [Fact]
    public void InstructionExecutionGuard()
    {
        Assert.True(
            TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(
                "Write an email asking Alex for the synthetic report.",
                "Sure, here's a draft: Hello Alex, please send the report.",
                ""),
            "Assistant-style execution should be rejected");

        Assert.True(
            TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(
                "Write a haiku about synthetic rain.",
                "Soft drizzle taps windows.",
                ""),
            "Low-overlap instruction execution should be rejected");

        Assert.False(
            TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(
                "Write an email asking Alex for the synthetic report.",
                "Write an email asking Alex for the synthetic report.",
                ""),
            "Faithful cleanup should preserve instruction wording");

        Assert.False(
            TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(
                "The synthetic launch is Friday.",
                "Sure, the synthetic launch is Friday.",
                ""),
            "Ordinary speech without an instruction marker should not trigger the guard");

        Assert.False(
            TranscriptOutputSanitizer.AppearsToHaveExecutedInstruction(
                "Translate the synthetic update.",
                "Aggiornamento sintetico.",
                "Italian"),
            "Explicit translation output should bypass the instruction guard");
    }
}
