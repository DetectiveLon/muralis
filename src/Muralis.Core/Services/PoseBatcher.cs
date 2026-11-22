using Muralis.Models;

namespace Muralis.Services;

/// <summary>Une pose demandée : écran (null = tous), image composée, position globale.</summary>
public sealed class PoseRequest(string? deviceId, string path, DesktopWallpaperPosition position)
{
    public string? DeviceId { get; } = deviceId;
    public string Path { get; } = path;
    public DesktopWallpaperPosition Position { get; } = position;

    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Succeed() => Completion.TrySetResult();

    public void Fail(Exception ex) => Completion.TrySetException(ex);
}

/// <summary>
/// Regroupe les poses demandées dans une courte fenêtre (plusieurs écrans dont les diaporamas
/// tombent ensemble, « Appliquer », changement d'écrans) pour les enchaîner d'un bloc : un seul
/// rafraîchissement perceptible du bureau au lieu d'un par écran (#41). L'exécution reprend
/// sur le contexte de la première demande (l'UI thread dans l'app : appel COM).
/// </summary>
public sealed class PoseBatcher(Action<IReadOnlyList<PoseRequest>> execute, TimeSpan window, Func<bool>? shouldDefer = null)
{
    private readonly object _lock = new();
    private List<PoseRequest>? _pending;

    public Task Enqueue(PoseRequest request)
    {
        bool first;
        lock (_lock)
        {
            first = _pending is null;
            (_pending ??= []).Add(request);
        }
        if (first)
            _ = FlushLaterAsync();
        return request.Completion.Task;
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(window);

        // Re-check immediately before the COM batch, not only before download/composition.
        // If a game became foreground during the 1.5s batching window, keep one pending
        // batch and retry after the fullscreen detector reports that it has ended.
        while (shouldDefer?.Invoke() == true)
            await Task.Delay(TimeSpan.FromMilliseconds(500));

        List<PoseRequest> batch;
        lock (_lock)
        {
            batch = _pending!;
            _pending = null;
        }

        try
        {
            execute(batch);
        }
        catch (Exception ex)
        {
            foreach (var request in batch)
                request.Fail(ex);
        }
        // Une requête que l'exécuteur aurait oubliée ne doit pas rester en attente.
        foreach (var request in batch)
            request.Fail(new InvalidOperationException("Pose not executed."));
    }
}
