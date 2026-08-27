using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using FreeFlow.Core.Models;
using FreeFlow.Core.Storage;
using Xunit;

namespace FreeFlow.Core.Tests;

/// <summary>Ported from <c>Tests/ModelConfigurationTests.swift</c>.</summary>
public class ModelConfigurationTests
{
    private static void AssertSameConfig(string alias, string canonical)
        => Assert.Equal(ModelConfiguration.Config(canonical), ModelConfiguration.Config(alias));

    [Fact]
    public void ProviderlessAliasesMatchCanonicalModels()
    {
        AssertSameConfig(" GPT-OSS-20B ", "openai/gpt-oss-20b");
        AssertSameConfig("gpt-oss-120b", "openai/gpt-oss-120b");
        AssertSameConfig("gpt-oss-safeguard-20b", "openai/gpt-oss-safeguard-20b");
        AssertSameConfig("qwen3-32b", "qwen/qwen3-32b");
        AssertSameConfig(" QWEN3.6-27B ", "qwen/qwen3.6-27b");
    }

    [Fact]
    public void KnownModelSettingsRemainStable()
    {
        var gptOss = ModelConfiguration.Config("openai/gpt-oss-20b");
        Assert.Equal(4096, gptOss.MaxCompletionTokens);
        Assert.Equal("low", gptOss.ReasoningEffort);
        Assert.False(gptOss.IncludeReasoning);
        Assert.False(gptOss.ShouldStripThinkTags);

        var qwen = ModelConfiguration.Config("qwen/qwen3.6-27b");
        Assert.Equal("none", qwen.ReasoningEffort);
        Assert.False(qwen.IncludeReasoning);
        Assert.True(qwen.ShouldStripThinkTags);

        var unknown = ModelConfiguration.Config("example/unknown-model");
        Assert.Null(unknown.MaxCompletionTokens);
        Assert.Null(unknown.ReasoningEffort);
        Assert.Null(unknown.IncludeReasoning);
        Assert.False(unknown.ShouldStripThinkTags);
    }

    [Fact]
    public void ModelListsAreConsistent()
    {
        Assert.Equal(ModelConfiguration.LlmModels.Count, ModelConfiguration.LlmModels.Distinct().Count());
        Assert.Equal(ModelConfiguration.VisionModels.Count, ModelConfiguration.VisionModels.Distinct().Count());
        Assert.Equal(
            ModelConfiguration.TranscriptionModels.Count,
            ModelConfiguration.TranscriptionModels.Distinct().Count());
        Assert.True(
            ModelConfiguration.VisionModels.All(ModelConfiguration.LlmModels.Contains),
            "Every vision model must also be selectable as an LLM");
    }

    [Fact]
    public void ThinkTagStripping()
    {
        Assert.Equal(
            "Visible output",
            ModelConfiguration.StripThinkTags("<think>hidden</think> Visible output"));
        Assert.Equal(
            "Result",
            ModelConfiguration.StripThinkTags("<think>one</think>\n<think>two</think>\nResult"));
        Assert.Equal("", ModelConfiguration.StripThinkTags("<think>unfinished"));
        // A think marker that is not at the start is ordinary output.
        Assert.Equal(
            "Ordinary output with a later <think> marker",
            ModelConfiguration.StripThinkTags("Ordinary output with a later <think> marker"));
    }
}

/// <summary>Ported from <c>Tests/LLMCooldownManagerTests.swift</c>.</summary>
public class LlmCooldownManagerTests
{
    private static (double Seconds, bool IsDaily) Cooldown(Dictionary<string, string> headers)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return LlmCooldownManager.RateLimitCooldown(response.Headers);
    }

    [Fact]
    public void DailyQuotaTakesPriority()
    {
        var result = Cooldown(new Dictionary<string, string>
        {
            ["x-ratelimit-remaining-requests"] = "0",
            ["x-ratelimit-reset-requests"] = "2m59.56s",
            ["retry-after"] = "2",
        });
        Assert.Equal(179.56, result.Seconds, 3);
        Assert.True(result.IsDaily);

        var nonExhausted = Cooldown(new Dictionary<string, string>
        {
            ["x-ratelimit-remaining-requests"] = "1",
            ["x-ratelimit-reset-requests"] = "1h",
            ["retry-after"] = "7.66",
        });
        Assert.Equal(7.66, nonExhausted.Seconds, 3);
        Assert.False(nonExhausted.IsDaily);
    }

    [Fact]
    public void RetryAndTokenDurations()
    {
        Assert.Equal(0.12, Cooldown(new() { ["retry-after"] = "120ms" }).Seconds, 3);
        Assert.Equal(3723.5, Cooldown(new() { ["retry-after"] = "1h2m3.5s" }).Seconds, 3);
        Assert.Equal(8.25, Cooldown(new() { ["x-ratelimit-reset-tokens"] = "8.25s" }).Seconds, 3);
    }

    [Theory]
    [InlineData("-3")]
    [InlineData("nan")]
    [InlineData("inf")]
    [InlineData("1d")]
    [InlineData("1h30")]
    [InlineData("")]
    public void MalformedDurationsUseSafeFallback(string invalid)
    {
        var result = Cooldown(new Dictionary<string, string> { ["retry-after"] = invalid });
        Assert.Equal(60, result.Seconds, 3);
        Assert.False(result.IsDaily);
    }

    [Fact]
    public void PersistenceKeyIsStable()
    {
        Assert.Equal(
            "llm_cooldown_expiry_openai/gpt-oss-20b",
            LlmCooldownManager.StoreKey("openai/gpt-oss-20b"));
    }

    [Fact]
    public void MinuteLevelCooldownStaysInMemoryAndExpires()
    {
        var now = DateTimeOffset.UnixEpoch;
        var store = new InMemoryKeyValueStore();
        var manager = new LlmCooldownManager(store, () => now);

        manager.SetCooldown("model-a", retryAfterSeconds: 30);
        Assert.True(manager.IsInCooldown("model-a"));
        // Short cooldowns must not be written to disk.
        Assert.Equal(0, store.GetDouble(LlmCooldownManager.StoreKey("model-a")));

        now = now.AddSeconds(31);
        Assert.False(manager.IsInCooldown("model-a"));
    }

    [Fact]
    public void DailyCooldownIsPersisted()
    {
        var now = DateTimeOffset.UnixEpoch;
        var store = new InMemoryKeyValueStore();
        var manager = new LlmCooldownManager(store, () => now);

        manager.SetCooldown("model-a", retryAfterSeconds: 7200);
        Assert.True(manager.IsInCooldown("model-a"));
        Assert.True(store.GetDouble(LlmCooldownManager.StoreKey("model-a")) > 0);

        // A short cooldown flagged daily is persisted too, so Settings can show it.
        manager.SetCooldown("model-b", retryAfterSeconds: 60, persist: true);
        Assert.True(store.GetDouble(LlmCooldownManager.StoreKey("model-b")) > 0);
    }

    [Fact]
    public void EffectivePrimaryFallsBackThenGivesUp()
    {
        var now = DateTimeOffset.UnixEpoch;
        var manager = new LlmCooldownManager(new InMemoryKeyValueStore(), () => now);

        Assert.Equal("primary", manager.EffectivePrimary("primary", "fallback"));

        manager.SetCooldown("primary", retryAfterSeconds: 30);
        Assert.Equal("fallback", manager.EffectivePrimary("primary", "fallback"));

        manager.SetCooldown("fallback", retryAfterSeconds: 30);
        Assert.Null(manager.EffectivePrimary("primary", "fallback"));
        Assert.Null(manager.EffectivePrimary("primary", null));
    }
}
