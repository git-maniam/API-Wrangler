namespace ApiWrangler.Domain;

public enum ScheduleType
{
    Interval,
    Cron
}

public enum ScheduleOverlapPolicy
{
    Skip,
    Queue,
    TerminateExisting
}

public enum ScheduleMissedRunPolicy
{
    SkipMissedIntervals,
    RunOnceImmediately
}

public enum NotificationSeverity
{
    Info,
    Warning,
    Failure,
    Recovery
}

public sealed record NotificationRoute(
    string RouteId,
    string WebhookUrl,
    string? SecretEnvVarName = null,
    bool SendOnFailure = true,
    bool SendOnRecovery = true,
    TimeSpan? CooldownPeriod = null
);

public sealed record ScheduleDefinition(
    string ScheduleId,
    string Name,
    string CollectionId,
    string? EnvironmentId = null,
    ScheduleType ScheduleType = ScheduleType.Interval,
    TimeSpan? Interval = null,
    string? CronExpression = null,
    string IanaTimeZoneId = "UTC",
    string? DesignatedDeploymentId = null,
    ScheduleOverlapPolicy OverlapPolicy = ScheduleOverlapPolicy.Skip,
    ScheduleMissedRunPolicy MissedRunPolicy = ScheduleMissedRunPolicy.SkipMissedIntervals,
    string? ApprovedCommitSha = null,
    IReadOnlyList<string>? RequiredRunnerCapabilities = null,
    IReadOnlyList<NotificationRoute>? NotificationRoutes = null,
    bool IsEnabled = true
);

public sealed record ScheduleExecutionRecord(
    string RunId,
    string ScheduleId,
    DateTimeOffset ScheduledTimeUtc,
    DateTimeOffset ActualExecutionTimeUtc,
    string TargetTimeZoneId,
    string Status,
    RunnerJobOutcome? Outcome = null
);

public sealed record NotificationEvent(
    string EventId,
    string ScheduleId,
    NotificationSeverity Severity,
    string Message,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string>? Details = null
);

public sealed record NotificationDeliveryDiagnostic(
    string RouteId,
    bool Delivered,
    int StatusCode,
    DateTimeOffset AttemptedAt,
    string? ErrorMessage = null,
    bool WasSuppressedByCooldown = false
);

