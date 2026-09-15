using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Win32.TaskScheduler;
using Retweak.Service.Configuration;
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace Retweak.Service.Core;

/// <summary>
/// Enforces <see cref="ScheduledTaskEntry"/> baseline: the task's Enabled flag, via the
/// COM Task Scheduler API. Like <see cref="ServiceBaselineManager"/> this only runs at
/// startup and on the audit cadence; there is no cheap change notification for a task's
/// enabled state. Retweak enforces existing tasks only — it never creates or deletes one,
/// since defining a task's triggers/actions is out of scope for a baseline enforcer.
/// </summary>
public sealed class ScheduledTaskBaselineManager
{
    private readonly RetweakOptions _options;
    private readonly ILogger<ScheduledTaskBaselineManager> _log;

    public ScheduledTaskBaselineManager(IOptions<RetweakOptions> options, ILogger<ScheduledTaskBaselineManager> log)
    {
        _options = options.Value;
        _log = log;
    }

    public PassReport ApplyBaseline(string source)
    {
        var sw = Stopwatch.StartNew();
        int examined = 0, repaired = 0, failed = 0;

        if (_options.ScheduledTaskEntries.Count == 0)
        {
            return new PassReport(0, 0, 0, sw.Elapsed);
        }

        try
        {
            using var taskService = new TaskService();

            foreach (ScheduledTaskEntry entry in _options.ScheduledTaskEntries)
            {
                examined++;
                if (ApplyOne(taskService, entry, source))
                {
                    repaired++;
                }
                else
                {
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scheduled task pass failed to start the Task Scheduler service.");
            failed += _options.ScheduledTaskEntries.Count - examined;
        }

        return new PassReport(examined, repaired, failed, sw.Elapsed);
    }

    private bool ApplyOne(TaskService taskService, ScheduledTaskEntry entry, string source)
    {
        string id = entry.DescribeAt();

        try
        {
            ScheduledTask? task = taskService.GetTask(entry.Path);
            if (task is null)
            {
                _log.LogError("Scheduled task {Id}: no such task.", id);
                return false;
            }

            if (task.Enabled == entry.Enabled)
            {
                return true;
            }

            _log.LogWarning(
                "Drift: {Id} enabled was {Actual}, expected {Desired}; trigger={Source}.",
                id, task.Enabled, entry.Enabled, source);

            task.Enabled = entry.Enabled;
            _log.LogInformation("Enforced: {Id} enabled set to {Desired}.", id, entry.Enabled);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed enforcing scheduled task {Id}.", id);
            return false;
        }
    }
}
