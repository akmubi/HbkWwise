using HbkWwise.Core;

namespace HbkWwise.Gui;

public sealed partial class MainWindow
{
    private async Task<bool> EnsureApplicationSetupAsync()
    {
        var candidate = settings.Copy();
        if (!EnsureRequiredTools(candidate))
        {
            return false;
        }

        settings.CopyFrom(candidate);
        SaveGuiSettingsQuietly();

        return await EnsureGameIndexAsync();
    }

    private bool EnsureRequiredTools(GuiSettings candidate)
    {
        SetBusy(true, "Checking required tools");
        try
        {
            candidate.RepakPath = RepakArchive.FindTool(
                candidate.RepakPath,
                "HBKWWISE_REPAK",
                "repak.exe");
            candidate.WwiserPath = WwiserClient.FindWwiser(candidate.WwiserPath);
            candidate.VgmstreamPath = VgmstreamClient.FindTool(candidate.VgmstreamPath);
            candidate.PythonPath = WwiserClient.FindPython(candidate.PythonPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            SetFailure("Required tool check failed", exception);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> EnsureGameIndexAsync()
    {
        var aesKey = CurrentAesKey();
        if (!GameIndexManager.TryResolveConfiguration(
                settings.PakDirectory,
                aesKey,
                out _,
                out var fingerprint))
        {
            return await ConfigureGameAsync();
        }

        var loadedExisting = File.Exists(GuiPaths.IndexPath)
            && await LoadIndexAsync(GuiPaths.IndexPath);
        if (loadedExisting
            && string.Equals(
                index?.SourceFingerprint,
                fingerprint,
                StringComparison.Ordinal))
        {
            var settingsChanged = !settings.GameSetupCompleted
                || !string.Equals(
                    settings.IndexSourceFingerprint,
                    fingerprint,
                    StringComparison.Ordinal);

            settings.GameSetupCompleted = true;
            settings.IndexSourceFingerprint = fingerprint;
            if (settingsChanged)
            {
                SaveGuiSettingsQuietly();
            }

            return true;
        }

        if (loadedExisting)
        {
            SetStatus("Hi-Fi RUSH game data changed; rebuilding the local cache");
        }

        var candidate = settings.Copy();
        var failure = await BuildAndUseGameIndexAsync(
            candidate,
            aesKey!,
            resetWorkspace: true);
        return failure is null || await ConfigureGameAsync(failure);
    }

    private async Task<bool> ConfigureGameAsync(string? initialError = null)
    {
        var error = initialError;
        while (true)
        {
            var request = await new GameSetupDialog(
                    settings.PakDirectory,
                    CurrentAesKey(),
                    error)
                .ShowDialog<GameSetupRequest?>(this);
            if (request is null)
            {
                SetStatus(
                    "Hi-Fi RUSH game setup is required before game audio can be used",
                    GuiLogLevel.Warning);
                return false;
            }

            var candidate = settings.Copy();
            candidate.PakDirectory = request.PakDirectory;
            candidate.AesKey = request.AesKey;

            error = await BuildAndUseGameIndexAsync(
                candidate,
                request.AesKey,
                resetWorkspace: true);
            if (error is null)
            {
                return true;
            }
        }
    }

    private async Task<string?> BuildAndUseGameIndexAsync(
        GuiSettings candidate,
        string aesKey,
        bool resetWorkspace)
    {
        indexOperation?.Cancel();
        using var operation = new CancellationTokenSource();
        indexOperation = operation;

        SetBusy(true, "Verifying game PAKs and preparing game data");
        try
        {
            var prepared = await GameIndexManager.BuildAsync(
                candidate.PakDirectory,
                aesKey,
                candidate.RepakPath,
                operation.Token);
            var searchIndex = await Task.Run(
                () => BuildBrowserSearchIndex(prepared.Index),
                operation.Token);

            index = prepared.Index;
            browserSearchIndex = searchIndex;
            timeline.SetNonAudioMediaIds(index.Media
                .Where(media => !media.IsPlayableAudio)
                .Select(media => media.Id));
            currentIndexPath = GuiPaths.IndexPath;

            eventStructures.Clear();
            if (resetWorkspace)
            {
                ResetTimelineForNavigation();
            }

            ClearBrowserSelection();
            ScheduleBrowserRefresh();
            RefreshClipCatalog();

            candidate.PakDirectory = prepared.PakDirectory;
            candidate.IndexSourceFingerprint = prepared.SourceFingerprint;
            candidate.GameSetupCompleted = true;
            settings.CopyFrom(candidate);
            SaveGuiSettingsQuietly();

            SetStatus(
                $"Game data ready: {index.Media.Length:N0} media, "
                + $"{index.Events.Length:N0} Events, {index.Banks.Length:N0} banks");
            return null;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Game setup cancelled");
            return "Game setup was cancelled.";
        }
        catch (Exception exception)
        {
            SetFailure("Game setup failed", exception);
            return FriendlyFailure(exception);
        }
        finally
        {
            if (ReferenceEquals(indexOperation, operation))
            {
                indexOperation = null;
                SetBusy(false);
            }
        }
    }

    private async Task OpenSettingsCoreAsync()
    {
        var updated = await new SettingsDialog(settings).ShowDialog<GuiSettings?>(this);
        if (updated is null)
        {
            return;
        }

        if (!EnsureRequiredTools(updated))
        {
            return;
        }

        if (!GameConfigurationChanged(settings, updated))
        {
            settings.CopyFrom(updated);
            SaveGuiSettingsQuietly();
            SetStatus("Preferences saved");
            return;
        }

        if (!await ConfirmProjectReplacementAsync())
        {
            return;
        }

        var projectToReload = currentProjectPath;
        var failure = await BuildAndUseGameIndexAsync(
            updated,
            updated.AesKey!,
            resetWorkspace: true);
        if (failure is not null)
        {
            return;
        }

        if (projectToReload is not null && File.Exists(projectToReload))
        {
            await LoadProjectAsync(projectToReload);
            SetStatus(
                $"Preferences saved, game data refreshed, and "
                + $"{Path.GetFileName(projectToReload)} reloaded");
            return;
        }

        SetStatus("Preferences saved and game data refreshed");
    }

    private static bool GameConfigurationChanged(GuiSettings current, GuiSettings updated) =>
        !SamePath(current.PakDirectory, updated.PakDirectory)
        || !string.Equals(
            current.AesKey?.Trim(),
            updated.AesKey?.Trim(),
            StringComparison.Ordinal);

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        return Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
