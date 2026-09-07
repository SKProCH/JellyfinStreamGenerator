using System.Reflection;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;

namespace Jellyfin.Plugin.StreamGenerator;

public interface IAdvancedTranscodeManager : ITranscodeManager
{
    IReadOnlyList<TranscodingJob> GetActiveTranscodingJobs();
}

public class AdvancedTranscodeManager(ITranscodeManager inner) : IAdvancedTranscodeManager
{
    private static readonly Lock InitLock = new();
    private static FieldInfo? _activeJobsField;
    private static object? _underlyingTranscodeManager;

    public IReadOnlyList<TranscodingJob> GetActiveTranscodingJobs()
    {
        if (_activeJobsField is null || _underlyingTranscodeManager is null)
        {
            lock (InitLock)
            {
                if (_activeJobsField is null || _underlyingTranscodeManager is null)
                {
                    (_underlyingTranscodeManager, _activeJobsField) = ResolveUnderlyingManagerAndField(inner);
                }
            }
        }

        var jobs = (List<TranscodingJob>)_activeJobsField.GetValue(_underlyingTranscodeManager)!;
        lock (jobs)
        {
            return [.. jobs];
        }
    }

    /// <summary>
    /// Finds the object with _activeTranscodingJobs in decorators chain
    /// </summary>
    private static (object Target, FieldInfo Field) ResolveUnderlyingManagerAndField(ITranscodeManager root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            // Find _activeTranscodingJobs field in a current type or base types
            var field = FindFieldInHierarchy(current.GetType(), "_activeTranscodingJobs");
            if (field is not null)
            {
                return (current, field);
            }

            // If field is not found search for ITranscodeManager fields in current object
            var fields = current.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var f in fields)
            {
                if (typeof(ITranscodeManager).IsAssignableFrom(f.FieldType))
                {
                    var val = f.GetValue(current);
                    if (val is not null)
                    {
                        queue.Enqueue(val);
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"Cannot find _activeTranscodingJobs in the ITranscodeManager decorator chain starting with {root.GetType().FullName}");
    }

    private static FieldInfo? FindFieldInHierarchy(Type type, string fieldName)
    {
        var current = type;
        while (current is not null && current != typeof(object))
        {
            var field = current.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (field is not null)
            {
                return field;
            }

            current = current.BaseType;
        }

        return null;
    }

    public TranscodingJob? GetTranscodingJob(string playSessionId)
        => inner.GetTranscodingJob(playSessionId);

    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type)
        => inner.GetTranscodingJob(path, type);

    public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
        => inner.PingTranscodingJob(playSessionId, isUserPaused);

    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
        => inner.KillTranscodingJobs(deviceId, playSessionId, deleteFiles);

    public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate)
        => inner.ReportTranscodingProgress(job, state, transcodingPosition, framerate, percentComplete, bytesTranscoded, bitRate);

    public Task<TranscodingJob> StartFfMpeg(StreamState state, string outputPath, string commandLineArguments, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null)
        => inner.StartFfMpeg(state, outputPath, commandLineArguments, userId, transcodingJobType, cancellationTokenSource, workingDirectory);

    public void OnTranscodeEndRequest(TranscodingJob job)
        => inner.OnTranscodeEndRequest(job);

    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
        => inner.OnTranscodeBeginRequest(path, type);

    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
        => inner.LockAsync(outputPath, cancellationToken);
}