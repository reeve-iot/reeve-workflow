using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Expressions;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionPreviewTimeZoneTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 5, 22, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo PlusFiveThirty =
        TimeZoneInfo.CreateCustomTimeZone("Preview+05:30", TimeSpan.FromMinutes(330), "Preview+05:30", "Preview+05:30");

    private sealed record HostZone(TimeZoneInfo Zone);

    [Fact]
    public async Task The_local_hour_is_read_in_the_configured_zone()
    {
        var preview = Pinned(new ExpressionPreviewService(null, [], PlusFiveThirty));

        var result = await preview.PreviewAsync("${now.localhour}", "workflow", new ServiceCollection().BuildServiceProvider());

        result.Error.Should().BeNull();
        result.Value.Should().Be((long)TimeZoneInfo.ConvertTime(Instant, PlusFiveThirty).Hour);
        result.Value.Should().NotBe((long)Instant.Hour);
    }

    [Fact]
    public async Task The_zone_the_lookup_answers_wins_over_the_setting_even_when_it_is_utc()
    {
        var preview = Pinned(new ExpressionPreviewService(null, [], PlusFiveThirty,
            (_, _) => Task.FromResult(TimeZoneInfo.Utc)));

        var result = await preview.PreviewAsync("${now.localhour}", "workflow", new ServiceCollection().BuildServiceProvider());

        result.Value.Should().Be((long)Instant.Hour);
    }

    [Fact]
    public async Task A_service_built_without_a_zone_reads_the_context_s_local_now()
    {
        var pinnedLocal = new DateTimeOffset(2026, 3, 5, 7, 0, 0, TimeSpan.Zero);
        var preview = new ExpressionPreviewService(null, []);
        preview.SetContext("utc", new ExpressionContext(now: Instant));
        preview.SetContext("local", new ExpressionContext(now: Instant, localNow: pinnedLocal));
        var services = new ServiceCollection().BuildServiceProvider();

        var utc = await preview.PreviewAsync("${now.localhour}", "utc", services);
        var local = await preview.PreviewAsync("${now.localhour}", "local", services);

        utc.Value.Should().Be((long)Instant.Hour);
        local.Value.Should().Be((long)pinnedLocal.Hour);
    }

    [Fact]
    public async Task A_context_that_pins_its_own_local_now_keeps_it_and_the_zone_is_not_asked()
    {
        var pinnedLocal = new DateTimeOffset(2026, 3, 5, 7, 0, 0, TimeSpan.Zero);
        var asked = 0;
        var preview = new ExpressionPreviewService(null, [], TimeZoneInfo.Utc, (_, _) =>
        {
            asked++;
            return Task.FromResult(PlusFiveThirty);
        });
        preview.SetContext("workflow", new ExpressionContext(now: Instant, localNow: pinnedLocal));

        var result = await preview.PreviewAsync("${now.localhour}", "workflow", new ServiceCollection().BuildServiceProvider());

        result.Value.Should().Be((long)pinnedLocal.Hour);
        asked.Should().Be(0);
    }

    [Fact]
    public async Task A_null_zone_from_the_setting_or_the_lookup_is_a_preview_error()
    {
        var fromSetting = Pinned(new ExpressionPreviewService(null, [], null!));
        var fromLookup = Pinned(new ExpressionPreviewService(null, [], TimeZoneInfo.Utc, (_, _) => Task.FromResult<TimeZoneInfo>(null!)));
        var services = new ServiceCollection().BuildServiceProvider();

        var settingResult = await fromSetting.PreviewAsync("${now.localhour}", "workflow", services);
        var lookupResult = await fromLookup.PreviewAsync("${now.localhour}", "workflow", services);

        settingResult.Error!.Message.Should().Be("The host has no time zone to read local time in.");
        lookupResult.Error!.Message.Should().Be("The host has no time zone to read local time in.");
    }

    [Fact]
    public async Task A_syntax_error_is_reported_before_the_zone_is_asked()
    {
        var preview = Pinned(new ExpressionPreviewService(null, [], TimeZoneInfo.Utc,
            (_, _) => throw new InvalidOperationException("no home")));

        var result = await preview.PreviewAsync("${now.localhour} +", "workflow", new ServiceCollection().BuildServiceProvider());

        result.Error!.RuleId.Should().Be(ReeveWorkflow.Core.Compilation.CompilationRules.ExpressionSyntax);
    }

    [Fact]
    public async Task The_zone_lookup_reads_the_services_the_preview_was_given_and_wins_over_the_setting()
    {
        var preview = Pinned(new ExpressionPreviewService(null, [], TimeZoneInfo.Utc,
            (services, _) => Task.FromResult(services.GetRequiredService<HostZone>().Zone)));
        var services = new ServiceCollection().AddSingleton(new HostZone(PlusFiveThirty)).BuildServiceProvider();

        var result = await preview.PreviewAsync("${now.localhour}", "workflow", services);

        result.Value.Should().Be((long)TimeZoneInfo.ConvertTime(Instant, PlusFiveThirty).Hour);
    }

    [Fact]
    public async Task A_failing_zone_lookup_is_a_preview_error_only_for_an_expression_that_reads_the_clock()
    {
        var preview = Pinned(new ExpressionPreviewService(null, [], TimeZoneInfo.Utc,
            (_, _) => throw new InvalidOperationException("no home")));
        var services = new ServiceCollection().BuildServiceProvider();

        var clock = await preview.PreviewAsync("${now.localhour}", "workflow", services);
        var arithmetic = await preview.PreviewAsync("1 + 2", "workflow", services);

        clock.Ok.Should().BeFalse();
        clock.Error!.Message.Should().Be("Could not resolve the host's time zone: no home");
        arithmetic.Value.Should().Be(3L);
    }

    private static ExpressionPreviewService Pinned(ExpressionPreviewService preview)
    {
        preview.SetContext("workflow", new ExpressionContext(now: Instant));
        return preview;
    }
}
