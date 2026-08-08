using System.Globalization;
using Remi.Application;

namespace Remi.Web;

public sealed class MonthlyActiveContractsCaptureWorker(
    IRemiMailStore mailStore,
    RemiMailEventService mailEvents,
    TimeProvider timeProvider,
    ILogger<MonthlyActiveContractsCaptureWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EvaluateAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await EvaluateAsync(stoppingToken);
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var template = await mailStore.GetTemplateAsync(MailEventTypes.MonthlyActiveContracts, cancellationToken);
            if (template is null) return;
            var timeZone = ResolveTimeZone(template.TimeZoneId);
            var nowUtc = timeProvider.GetUtcNow();
            var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
            var currentMonth = new DateOnly(localNow.Year, localNow.Month, 1);
            var state = await mailStore.GetSchedulerPeriodAsync(MailEventTypes.MonthlyActiveContracts, cancellationToken);
            if (!TryPeriod(state, out var evaluatedMonth))
            {
                await mailStore.SetSchedulerPeriodAsync(
                    MailEventTypes.MonthlyActiveContracts,
                    currentMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    nowUtc,
                    cancellationToken);
                logger.LogInformation("Initialised monthly active-contract mail scheduling at {Period}; no historical messages were generated.", currentMonth.ToString("yyyy-MM"));
                return;
            }
            if (!template.Enabled || template.ScheduleDay is null || template.ScheduleTimeLocal is null) return;

            var candidate = evaluatedMonth.AddMonths(1);
            while (candidate <= currentMonth)
            {
                var day = Math.Min(template.ScheduleDay.Value, DateTime.DaysInMonth(candidate.Year, candidate.Month));
                var localSchedule = new DateTime(
                    candidate.Year,
                    candidate.Month,
                    day,
                    template.ScheduleTimeLocal.Value.Hour,
                    template.ScheduleTimeLocal.Value.Minute,
                    0,
                    DateTimeKind.Unspecified);
                var scheduledUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localSchedule, timeZone), TimeSpan.Zero);
                if (scheduledUtc > nowUtc) return;

                var sourcePeriod = candidate.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
                var result = await mailEvents.CaptureMonthlyActiveContractsAsync(sourcePeriod, scheduledUtc, cancellationToken);
                if (!result.Succeeded)
                {
                    logger.LogWarning("Monthly active-contract mail for {Period} was not captured: {Message}", sourcePeriod, result.Message);
                    return;
                }

                await mailStore.SetSchedulerPeriodAsync(
                    MailEventTypes.MonthlyActiveContracts,
                    candidate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    nowUtc,
                    cancellationToken);
                logger.LogInformation("Captured monthly active-contract mail for {Period}. External delivery remains disabled.", sourcePeriod);
                candidate = candidate.AddMonths(1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Monthly active-contract mail evaluation failed. No external delivery was attempted.");
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    private static bool TryPeriod(string? value, out DateOnly period) =>
        DateOnly.TryParseExact($"{value}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out period);
}
