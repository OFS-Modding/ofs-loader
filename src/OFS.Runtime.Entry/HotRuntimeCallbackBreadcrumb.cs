using System.IO.MemoryMappedFiles;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed class HotRuntimeCallbackBreadcrumb : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _templates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ActiveFrame> _active = [];
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private FileStream? _backingStream;
    private long _nextLeaseId;

    internal ModLoadJournal? Initialize(string safetyRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safetyRoot);
        lock (_gate)
        {
            DisableCore();
            Directory.CreateDirectory(safetyRoot);
            var path = Path.Combine(safetyRoot, HotCrashBreadcrumbCodec.FileName);
            ModLoadJournal? interrupted = null;
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > HotCrashBreadcrumbCodec.FileSize)
                {
                    throw new InvalidDataException(
                        $"Hot callback breadcrumb exceeds {HotCrashBreadcrumbCodec.FileSize} bytes.");
                }
                var bytes = File.ReadAllBytes(path);
                if (!HotCrashBreadcrumbCodec.TryReadActive(
                        bytes,
                        out interrupted,
                        out var error) &&
                    error.Length != 0)
                {
                    throw new InvalidDataException(error);
                }
            }

            _backingStream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            _backingStream.SetLength(HotCrashBreadcrumbCodec.FileSize);
            _mapping = MemoryMappedFile.CreateFromFile(
                _backingStream,
                null,
                HotCrashBreadcrumbCodec.FileSize,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true);
            _view = _mapping.CreateViewAccessor(
                0,
                HotCrashBreadcrumbCodec.FileSize,
                MemoryMappedFileAccess.ReadWrite);
            _view.Write(HotCrashBreadcrumbCodec.CommitOffset, 0);
            _view.Flush();
            return interrupted;
        }
    }

    internal HotRuntimeCallbackLease Enter(
        string modId,
        string version,
        string manifestPath,
        string assemblyPath,
        string phase)
    {
        lock (_gate)
        {
            if (_view is null)
            {
                return default;
            }
            var normalizedPhase = NormalizePhase(phase);
            var key = $"{modId}\n{normalizedPhase}";
            if (!_templates.TryGetValue(key, out var template))
            {
                template = HotCrashBreadcrumbCodec.CreateInactiveTemplate(new ModLoadJournal
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    ModId = modId,
                    Version = version,
                    Phase = $"callback:hot:{normalizedPhase}",
                    ManifestPath = manifestPath,
                    AssemblyPath = assemblyPath,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    ProcessId = Environment.ProcessId,
                });
                _templates.Add(key, template);
            }

            var leaseId = checked(++_nextLeaseId);
            _active.Add(new ActiveFrame(
                leaseId,
                Environment.CurrentManagedThreadId,
                template));
            PublishCurrent();
            return new HotRuntimeCallbackLease(this, leaseId);
        }
    }

    internal void Exit(long leaseId)
    {
        lock (_gate)
        {
            var index = _active.FindIndex(frame => frame.LeaseId == leaseId);
            if (index < 0)
            {
                return;
            }
            _active.RemoveAt(index);
            PublishCurrent();
        }
    }

    internal void Disable()
    {
        lock (_gate)
        {
            DisableCore();
        }
    }

    public void Dispose() => Disable();

    private void PublishCurrent()
    {
        var view = _view;
        if (view is null)
        {
            return;
        }
        view.Write(HotCrashBreadcrumbCodec.CommitOffset, 0);
        if (_active.Count == 0)
        {
            return;
        }

        var threadId = _active[0].ThreadId;
        for (var index = 1; index < _active.Count; ++index)
        {
            if (_active[index].ThreadId != threadId)
            {
                // Concurrent callbacks are ambiguous. Preserve no accusation.
                return;
            }
        }

        var template = _active[^1].Template;
        view.WriteArray(0, template, 0, template.Length);
        Thread.MemoryBarrier();
        view.Write(HotCrashBreadcrumbCodec.CommitOffset, 1);
    }

    private void DisableCore()
    {
        _active.Clear();
        _templates.Clear();
        _nextLeaseId = 0;
        _view?.Dispose();
        _mapping?.Dispose();
        _backingStream?.Dispose();
        _view = null;
        _mapping = null;
        _backingStream = null;
    }

    private static string NormalizePhase(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        var normalized = new string(phase
            .Select(character => char.IsControl(character) ? '_' : character)
            .Take(87)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException("Hot callback phase cannot be empty.", nameof(phase))
            : normalized;
    }

    private sealed record ActiveFrame(long LeaseId, int ThreadId, byte[] Template);
}

internal readonly struct HotRuntimeCallbackLease(
    HotRuntimeCallbackBreadcrumb? owner,
    long leaseId) : IDisposable
{
    public void Dispose() => owner?.Exit(leaseId);
}
