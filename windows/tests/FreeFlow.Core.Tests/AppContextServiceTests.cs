using FreeFlow.Core.Context;
using FreeFlow.Core.Models;
using Xunit;

namespace FreeFlow.Core.Tests;

/// <summary>Ported from <c>Tests/AppContextServiceTests.swift</c>.</summary>
public class AppContextServiceTests
{
    [Fact]
    public void RawOutputIsTrimmedToTwoSentences()
    {
        var output =
            "The user is replying to an email about the product launch. " +
            "They likely intend to confirm the next steps. " +
            "This third sentence should be dropped.";

        var summary = AppContextService.ActivitySummary(output, "qwen/qwen3.6-27b");

        Assert.Equal(
            "The user is replying to an email about the product launch. " +
            "They likely intend to confirm the next steps.",
            summary);
    }

    [Fact]
    public void ReasoningOutputIsStripped()
    {
        var output =
            "<think>\nHidden chain of thought should never appear in context.\n" +
            "It contains misleading details.\n</think>\n" +
            "The user is editing a project note in FreeFlow. They likely intend to tighten the release wording.";

        var summary = AppContextService.ActivitySummary(output, "qwen/qwen3.6-27b");

        Assert.Equal(
            "The user is editing a project note in FreeFlow. They likely intend to tighten the release wording.",
            summary);
        Assert.DoesNotContain("Hidden chain of thought", summary);
    }

    [Fact]
    public void NonStrippingModelPreservesExistingBehavior()
    {
        var output = "<think>Visible for non-stripping models.</think> The user is writing a status update.";

        var summary = AppContextService.ActivitySummary(
            output, "meta-llama/llama-4-scout-17b-16e-instruct");

        Assert.Equal(output, summary);
    }

    [Fact]
    public void EmptyOutputYieldsNoSummary()
    {
        Assert.Null(AppContextService.ActivitySummary("   \n  ", "qwen/qwen3.6-27b"));
        // A response that is nothing but reasoning leaves nothing usable behind.
        Assert.Null(AppContextService.ActivitySummary("<think>only reasoning</think>", "qwen/qwen3.6-27b"));
    }

    [Theory]
    [InlineData("qwen/qwen3-32b")]
    [InlineData("meta-llama/llama-4-scout-17b-16e-instruct")]
    [InlineData("llama-3.1-8b-instant")]
    [InlineData("llama-3.3-70b-versatile")]
    public void DeprecatedModelsAreNotOfferedInThePicker(string model)
        => Assert.DoesNotContain(model, ModelConfiguration.LlmModels);

    [Fact]
    public void CurrentFallbackModelIsOffered()
        => Assert.Contains("qwen/qwen3.6-27b", ModelConfiguration.LlmModels);

    [Fact]
    public void ContextModelDisablesReasoning()
    {
        var config = ModelConfiguration.Config("qwen/qwen3.6-27b");
        Assert.Equal("none", config.ReasoningEffort);
        Assert.False(config.IncludeReasoning);
    }
}
