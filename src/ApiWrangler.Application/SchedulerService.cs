using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class SchedulerEngine
{
    private readonly ConcurrentDictionary<string, ScheduleDefinition> schedules = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastRunTimes = new();

    public string CurrentDeploymentId { get; }

    public SchedulerEngine(string? currentDeploymentId = null)
    {
        CurrentDeploymentId = currentDeploymentId ?? Environment.MachineName.ToLowerInvariant();
    }

    public void AddOrUpdateSchedule(ScheduleDefinition schedule)
    {
        schedules[schedule.ScheduleId] = schedule;
    }

    public bool ShouldExecuteOnThisDeployment(ScheduleDefinition schedule)
    {
        if (!schedule.IsEnabled)
        {
            return false;
        }

        // Section 21.2: Opening the same repository on multiple clients must not duplicate monitoring.
        if (!string.IsNullOrWhiteSpace(schedule.DesignatedDeploymentId))
        {
            return string.Equals(schedule.DesignatedDeploymentId, CurrentDeploymentId, StringComparison.OrdinalIgnoreCase);
        }

        // If no designated deployment is set, desktop/host scheduling is opt-in
        return true;
    }

    public ScheduleExecutionRecord EvaluateTick(
        ScheduleDefinition schedule,
        DateTimeOffset tickUtc,
        Func<string, bool> isJobInProgress,
        Action<RunnerJobDefinition> enqueueAction)
    {
        // 1. Ownership check
        if (!ShouldExecuteOnThisDeployment(schedule))
        {
            return new ScheduleExecutionRecord(
                RunId: Guid.NewGuid().ToString("N"),
                ScheduleId: schedule.ScheduleId,
                ScheduledTimeUtc: tickUtc,
                ActualExecutionTimeUtc: tickUtc,
                TargetTimeZoneId: schedule.IanaTimeZoneId,
                Status: "SkippedOwnership"
            );
        }

        // 2. Overlap check
        if (isJobInProgress(schedule.ScheduleId))
        {
            if (schedule.OverlapPolicy == ScheduleOverlapPolicy.Skip)
            {
                return new ScheduleExecutionRecord(
                    RunId: Guid.NewGuid().ToString("N"),
                    ScheduleId: schedule.ScheduleId,
                    ScheduledTimeUtc: tickUtc,
                    ActualExecutionTimeUtc: tickUtc,
                    TargetTimeZoneId: schedule.IanaTimeZoneId,
                    Status: "SkippedOverlap"
                );
            }
        }

        // 3. Time zone and missed run semantics
        TimeZoneInfo targetTz;
        try
        {
            targetTz = TimeZoneInfo.FindSystemTimeZoneById(schedule.IanaTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            targetTz = TimeZoneInfo.Utc;
        }

        var localTargetTime = TimeZoneInfo.ConvertTime(tickUtc, targetTz);

        // Check missed intervals (downtime recovery)
        if (lastRunTimes.TryGetValue(schedule.ScheduleId, out var lastRunUtc) && schedule.Interval.HasValue)
        {
            var elapsed = tickUtc - lastRunUtc;
            if (elapsed > schedule.Interval.Value * 2 && schedule.MissedRunPolicy == ScheduleMissedRunPolicy.SkipMissedIntervals)
            {
                // Advance last run time to skip intermediate missed backlog
                lastRunTimes[schedule.ScheduleId] = tickUtc;
            }
        }
        else
        {
            lastRunTimes[schedule.ScheduleId] = tickUtc;
        }

        // 4. Enqueue execution with pinned approved commit
        var runId = $"run_{Guid.NewGuid():N}";
        var jobDef = new RunnerJobDefinition(
            JobId: runId,
            ProjectId: schedule.ScheduleId,
            RepositoryUrl: ".",
            ExactCommitSha: schedule.ApprovedCommitSha ?? "HEAD",
            ExecutionType: "CollectionRun",
            SelectedEntities: new[] { schedule.CollectionId },
            EnvironmentName: schedule.EnvironmentId,
            RequiredCapabilities: schedule.RequiredRunnerCapabilities
        );

        enqueueAction(jobDef);

        return new ScheduleExecutionRecord(
            RunId: runId,
            ScheduleId: schedule.ScheduleId,
            ScheduledTimeUtc: tickUtc,
            ActualExecutionTimeUtc: tickUtc,
            TargetTimeZoneId: schedule.IanaTimeZoneId,
            Status: "Dispatched",
            Outcome: RunnerJobOutcome.Queued
        );
    }
}

public sealed class NotificationDispatcher
{
    private readonly HttpClient httpClient;
    private readonly ConcurrentDictionary<(string ScheduleId, string RouteId), DateTimeOffset> lastFailureTimes = new();
    private readonly ConcurrentDictionary<string, RunnerJobOutcome> lastKnownOutcomes = new();

    public NotificationDispatcher(HttpClient? client = null)
    {
        this.httpClient = client ?? new HttpClient();
    }

    public async Task<IReadOnlyList<NotificationDeliveryDiagnostic>> DispatchAsync(
        ScheduleDefinition schedule,
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<NotificationDeliveryDiagnostic>();
        if (schedule.NotificationRoutes is null || schedule.NotificationRoutes.Count == 0)
        {
            return diagnostics;
        }

        var previousOutcome = lastKnownOutcomes.TryGetValue(schedule.ScheduleId, out var prev) ? prev : (RunnerJobOutcome?)null;

        foreach (var route in schedule.NotificationRoutes)
        {
            var key = (schedule.ScheduleId, route.RouteId);
            var now = DateTimeOffset.UtcNow;

            // 1. Failure cooldown deduplication
            if (notification.Severity == NotificationSeverity.Failure)
            {
                if (!route.SendOnFailure) continue;

                if (route.CooldownPeriod.HasValue && lastFailureTimes.TryGetValue(key, out var lastSent))
                {
                    if (now - lastSent < route.CooldownPeriod.Value)
                    {
                        diagnostics.Add(new NotificationDeliveryDiagnostic(
                            RouteId: route.RouteId,
                            Delivered: false,
                            StatusCode: 0,
                            AttemptedAt: now,
                            ErrorMessage: "Suppressed by cooldown period.",
                            WasSuppressedByCooldown: true
                        ));
                        continue;
                    }
                }

                lastFailureTimes[key] = now;
            }

            // 2. Recovery notification check
            if (notification.Severity == NotificationSeverity.Recovery)
            {
                if (!route.SendOnRecovery) continue;
                if (previousOutcome != RunnerJobOutcome.Failed && previousOutcome != RunnerJobOutcome.Indeterminate)
                {
                    // No failure to recover from
                    continue;
                }
            }

            // 3. Resolve host-local notification secret without committing credentials to Git
            string? secret = null;
            if (!string.IsNullOrWhiteSpace(route.SecretEnvVarName))
            {
                secret = Environment.GetEnvironmentVariable(route.SecretEnvVarName);
            }

            // 4. Send HTTP notification webhook
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route.WebhookUrl);
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                }

                var payload = JsonSerializer.Serialize(notification);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                diagnostics.Add(new NotificationDeliveryDiagnostic(
                    RouteId: route.RouteId,
                    Delivered: response.IsSuccessStatusCode,
                    StatusCode: (int)response.StatusCode,
                    AttemptedAt: now,
                    ErrorMessage: response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                ));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new NotificationDeliveryDiagnostic(
                    RouteId: route.RouteId,
                    Delivered: false,
                    StatusCode: 0,
                    AttemptedAt: now,
                    ErrorMessage: ex.Message
                ));
            }
        }

        // Update state
        if (notification.Severity == NotificationSeverity.Failure)
        {
            lastKnownOutcomes[schedule.ScheduleId] = RunnerJobOutcome.Failed;
        }
        else if (notification.Severity == NotificationSeverity.Recovery)
        {
            lastKnownOutcomes[schedule.ScheduleId] = RunnerJobOutcome.Passed;
        }

        return diagnostics;
    }
}

