using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class SchedulerServiceTests
{
    [Fact]
    public void Scheduler_DesignatedDeploymentOwnership_PreventsDuplicateRunsAcrossMultipleClients()
    {
        var schedule = new ScheduleDefinition(
            ScheduleId: "sched-daily-smoke",
            Name: "Daily Smoke Test",
            CollectionId: "coll-smoke",
            Interval: TimeSpan.FromMinutes(15),
            DesignatedDeploymentId: "runner-node-production-01",
            ApprovedCommitSha: "abc1234567890abcdef1234567890abcdef12345"
        );

        // Simulation 1: Developer 1 laptop
        var laptopAlice = new SchedulerEngine(currentDeploymentId: "laptop-alice");
        bool aliceEnqueued = false;
        var aliceRecord = laptopAlice.EvaluateTick(schedule, DateTimeOffset.UtcNow, _ => false, _ => aliceEnqueued = true);
        Assert.Equal("SkippedOwnership", aliceRecord.Status);
        Assert.False(aliceEnqueued);

        // Simulation 2: Developer 2 laptop
        var laptopBob = new SchedulerEngine(currentDeploymentId: "laptop-bob");
        bool bobEnqueued = false;
        var bobRecord = laptopBob.EvaluateTick(schedule, DateTimeOffset.UtcNow, _ => false, _ => bobEnqueued = true);
        Assert.Equal("SkippedOwnership", bobRecord.Status);
        Assert.False(bobEnqueued);

        // Simulation 3: Designated runner node
        var prodRunner = new SchedulerEngine(currentDeploymentId: "runner-node-production-01");
        bool prodEnqueued = false;
        RunnerJobDefinition? enqueuedJob = null;
        var prodRecord = prodRunner.EvaluateTick(schedule, DateTimeOffset.UtcNow, _ => false, job =>
        {
            prodEnqueued = true;
            enqueuedJob = job;
        });

        Assert.Equal("Dispatched", prodRecord.Status);
        Assert.True(prodEnqueued);
        Assert.NotNull(enqueuedJob);
        Assert.Equal("abc1234567890abcdef1234567890abcdef12345", enqueuedJob.ExactCommitSha);
    }

    [Fact]
    public void Scheduler_TimeZonesAndDowntime_SkipsMissedIntervalsWithoutBacklog()
    {
        var schedule = new ScheduleDefinition(
            ScheduleId: "sched-downtime-recovery",
            Name: "Interval Monitor",
            CollectionId: "coll-1",
            Interval: TimeSpan.FromMinutes(5),
            IanaTimeZoneId: "UTC",
            MissedRunPolicy: ScheduleMissedRunPolicy.SkipMissedIntervals
        );

        var engine = new SchedulerEngine("local-engine");
        int dispatchedCount = 0;

        var t0 = DateTimeOffset.UtcNow;
        engine.EvaluateTick(schedule, t0, _ => false, _ => dispatchedCount++);
        Assert.Equal(1, dispatchedCount);

        // Simulate 2 hours of downtime (24 missed intervals)
        var tDowntimeLater = t0.AddHours(2);
        var record = engine.EvaluateTick(schedule, tDowntimeLater, _ => false, _ => dispatchedCount++);

        // Dispatches the single current run, does NOT flood queue with 24 backlogged runs
        Assert.Equal("Dispatched", record.Status);
        Assert.Equal(2, dispatchedCount);
    }

    [Fact]
    public void Scheduler_OverlapPrevention_SkipsExecutionWhenPreviousRunIsActive()
    {
        var schedule = new ScheduleDefinition(
            ScheduleId: "sched-no-overlap",
            Name: "Heavy Load Run",
            CollectionId: "coll-heavy",
            Interval: TimeSpan.FromMinutes(1),
            OverlapPolicy: ScheduleOverlapPolicy.Skip
        );

        var engine = new SchedulerEngine("local-engine");
        int dispatchCount = 0;

        // Tick 1: No job in progress
        var rec1 = engine.EvaluateTick(schedule, DateTimeOffset.UtcNow, isJobInProgress: _ => false, _ => dispatchCount++);
        Assert.Equal("Dispatched", rec1.Status);
        Assert.Equal(1, dispatchCount);

        // Tick 2: Previous run is still in progress -> skipped
        var rec2 = engine.EvaluateTick(schedule, DateTimeOffset.UtcNow.AddMinutes(1), isJobInProgress: _ => true, _ => dispatchCount++);
        Assert.Equal("SkippedOverlap", rec2.Status);
        Assert.Equal(1, dispatchCount); // Count unchanged
    }

    [Fact]
    public async Task NotificationDispatcher_EnforcesCooldown_DeduplicatesAlerts()
    {
        await using var mock = new MockEngine();
        mock.AddRule(new MockRule(
            Id: "webhook-route",
            Name: "Notification Webhook",
            Method: "POST",
            PathPattern: "/webhook/alert",
            ResponseStatusCode: 200,
            ResponseBody: "{\"accepted\": true}"
        ));
        await mock.StartAsync();

        try
        {
            var route = new NotificationRoute(
                RouteId: "pager-1",
                WebhookUrl: mock.ListeningUrl + "/webhook/alert",
                SendOnFailure: true,
                CooldownPeriod: TimeSpan.FromMinutes(15)
            );

            var schedule = new ScheduleDefinition(
                ScheduleId: "sched-notif-cooldown",
                Name: "Alerting Schedule",
                CollectionId: "coll-alerts",
                NotificationRoutes: new[] { route }
            );

            var dispatcher = new NotificationDispatcher();

            var failure1 = new NotificationEvent(
                EventId: "ev-1",
                ScheduleId: schedule.ScheduleId,
                Severity: NotificationSeverity.Failure,
                Message: "Collection smoke run failed 3 assertions.",
                TimestampUtc: DateTimeOffset.UtcNow
            );

            // 1. First failure -> Delivered
            var diag1 = await dispatcher.DispatchAsync(schedule, failure1);
            Assert.Single(diag1);
            Assert.True(diag1[0].Delivered);
            Assert.False(diag1[0].WasSuppressedByCooldown);

            // 2. Second failure within cooldown period -> Suppressed
            var failure2 = new NotificationEvent(
                EventId: "ev-2",
                ScheduleId: schedule.ScheduleId,
                Severity: NotificationSeverity.Failure,
                Message: "Collection smoke run failed again.",
                TimestampUtc: DateTimeOffset.UtcNow
            );

            var diag2 = await dispatcher.DispatchAsync(schedule, failure2);
            Assert.Single(diag2);
            Assert.False(diag2[0].Delivered);
            Assert.True(diag2[0].WasSuppressedByCooldown);
            Assert.Contains("Suppressed by cooldown", diag2[0].ErrorMessage);
        }
        finally
        {
            await mock.StopAsync();
        }
    }

    [Fact]
    public async Task NotificationDispatcher_SendsRecoveryAfterFailure_AndUsesHostSecrets()
    {
        await using var mock = new MockEngine();
        mock.AddRule(new MockRule(
            Id: "webhook-recovery",
            Name: "Recovery Webhook",
            Method: "POST",
            PathPattern: "/webhook/recovery",
            ResponseStatusCode: 200,
            ResponseBody: "{\"ok\": true}"
        ));
        await mock.StartAsync();

        const string secretKey = "API_WRANGLER_NOTIFY_RECOVERY_SECRET";
        Environment.SetEnvironmentVariable(secretKey, "super-secret-token-xyz");

        try
        {
            var route = new NotificationRoute(
                RouteId: "recovery-route",
                WebhookUrl: mock.ListeningUrl + "/webhook/recovery",
                SecretEnvVarName: secretKey,
                SendOnFailure: true,
                SendOnRecovery: true
            );

            var schedule = new ScheduleDefinition(
                ScheduleId: "sched-recovery-test",
                Name: "Recovery Test Schedule",
                CollectionId: "coll-recovery",
                NotificationRoutes: new[] { route }
            );

            var dispatcher = new NotificationDispatcher();

            // 1. Initial recovery without failure is ignored
            var initialRec = new NotificationEvent(
                EventId: "rec-0",
                ScheduleId: schedule.ScheduleId,
                Severity: NotificationSeverity.Recovery,
                Message: "Recovered",
                TimestampUtc: DateTimeOffset.UtcNow
            );
            var initialDiag = await dispatcher.DispatchAsync(schedule, initialRec);
            Assert.Empty(initialDiag);

            // 2. Failure occurs
            var failEvt = new NotificationEvent(
                EventId: "fail-1",
                ScheduleId: schedule.ScheduleId,
                Severity: NotificationSeverity.Failure,
                Message: "System is failing",
                TimestampUtc: DateTimeOffset.UtcNow
            );
            var failDiag = await dispatcher.DispatchAsync(schedule, failEvt);
            Assert.Single(failDiag);
            Assert.True(failDiag[0].Delivered);

            // 3. Recovery follows failure -> Delivered
            var recEvt = new NotificationEvent(
                EventId: "rec-1",
                ScheduleId: schedule.ScheduleId,
                Severity: NotificationSeverity.Recovery,
                Message: "All 10 tests passing again. System recovered.",
                TimestampUtc: DateTimeOffset.UtcNow
            );
            var recDiag = await dispatcher.DispatchAsync(schedule, recEvt);
            Assert.Single(recDiag);
            Assert.True(recDiag[0].Delivered);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretKey, null);
            await mock.StopAsync();
        }
    }
}

