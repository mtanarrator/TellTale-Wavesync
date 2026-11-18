using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ScriptPortal.Vegas;
using VegasAudioSync.Interop;
using VegasAudioSync.Models;
using VegasAudioSync.Services;

namespace VegasAudioSync.UI
{
    public partial class VegasAudioSyncControl : UserControl
    {
        private const int MaxLogLines = 2000;

        private readonly Vegas _vegas;
        private readonly NativeSyncEngine _engine;
        private readonly Queue<string> _logLines = new Queue<string>();
        private volatile bool _cancelRequested;
        private string _cachedFfmpegPath;

        public VegasAudioSyncControl() : this(null) { }

        public VegasAudioSyncControl(Vegas vegas)
        {
            InitializeComponent();
            _vegas = vegas;
            _engine = new NativeSyncEngine();

            Loaded += OnControlLoaded;
        }

        private async void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string ver = _engine.GetEngineVersion();
                if (!string.IsNullOrEmpty(ver))
                {
                    TxtStatus.Text = "Ready · " + ver;
                }
            }
            catch (Exception ex)
            {
                AppendLog("Failed to load native engine: " + ex.Message);
            }

            RefreshTimelineTracks();

            if (string.IsNullOrEmpty(_cachedFfmpegPath))
            {
                _cachedFfmpegPath = await Task.Run(() => FindFfmpegBinary());
            }
        }

        /// <summary>Identifies a concrete timeline track in the master dropdown.</summary>
        private sealed class MasterChoice
        {
            public string TrackId;
            public bool IsVideo;
        }

        private void RefreshTimelineTracks()
        {
            if (_vegas == null || _vegas.Project == null) return;

            // Preserve the operator's choice across refreshes.
            string previousTrackId = null;
            var previousChoice = (CmbMasterTrack.SelectedItem as ComboBoxItem)?.Tag as MasterChoice;
            if (previousChoice != null)
            {
                previousTrackId = previousChoice.TrackId;
            }

            CmbMasterTrack.Items.Clear();

            var autoItem = new ComboBoxItem
            {
                Content = "Auto-Detect (Longest Continuous Take)",
                Foreground = System.Windows.Media.Brushes.Black,
                Tag = null,
                ToolTip = "Automatically pick the event with the longest continuous media span."
            };
            CmbMasterTrack.Items.Add(autoItem);
            CmbMasterTrack.SelectedItem = autoItem;

            foreach (Track track in _vegas.Project.Tracks)
            {
                if (!track.IsAudio() && !track.IsVideo())
                    continue;

                string kind = track.IsAudio() ? "Audio" : "Video";
                string kindShort = track.IsAudio() ? "A" : "V";
                string name = string.IsNullOrEmpty(track.Name) ? $"Track {track.Index + 1}" : track.Name;

                int clipCount = 0;
                double longestSec = 0.0;
                foreach (TrackEvent ev in track.Events)
                {
                    clipCount++;
                    try
                    {
                        Take take = ev.ActiveTake;
                        if (take == null || take.Media == null) continue;

                        double available = take.AvailableLength.ToMilliseconds() / 1000.0;
                        if (available > longestSec) longestSec = available;
                    }
                    catch
                    {
                        // Metadata unavailable for this event.
                    }
                }

                var choice = new MasterChoice
                {
                    TrackId = $"Track_{track.Index}",
                    IsVideo = track.IsVideo()
                };

                string shortName = ShortenText(name, 20);
                string durationText = FormatDuration(longestSec);
                var item = new ComboBoxItem
                {
                    Content = $"[{kindShort}] {track.Index + 1}: {shortName} · {clipCount}c · {durationText}",
                    Foreground = System.Windows.Media.Brushes.Black,
                    Tag = choice,
                    ToolTip =
                        $"Use the longest take on this {kind.ToLowerInvariant()} track as the master reference.\n" +
                        $"Track {track.Index + 1}: {name}\n" +
                        $"{clipCount} clip(s), longest {durationText}"
                };

                CmbMasterTrack.Items.Add(item);

                if (previousTrackId != null && previousTrackId == choice.TrackId)
                {
                    CmbMasterTrack.SelectedItem = item;
                }
            }
        }

        private static string ShortenText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, Math.Max(1, maxLength - 1)).TrimEnd() + "…";
        }

        private static string FormatDuration(double seconds)
        {
            long total = (long)Math.Max(0, seconds);
            long h = total / 3600;
            long m = (total % 3600) / 60;
            long s = total % 60;
            if (h > 0) return $"{h}h{m:00}m";
            if (m > 0) return $"{m}m{s:00}s";
            return $"{s}s";
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshTimelineTracks();
            AppendLog("Timeline refreshed.");
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cancelRequested = true;
            TxtStatus.Text = "Cancelling...";
            BtnCancel.IsEnabled = false;
        }

        private async void BtnSync_Click(object sender, RoutedEventArgs e)
        {
            if (_vegas == null || _vegas.Project == null)
            {
                MessageBox.Show("Please open a VEGAS Pro project first.", "TellTale WaveSync", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _cancelRequested = false;
            BtnSync.IsEnabled = false;
            BtnRefresh.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            PrgSync.Value = 0;
            TxtStatus.Text = "Analyzing timeline...";
            ClearLog();

            try
            {
                bool selectedOnly = ChkSelectedOnly.IsChecked == true;
                bool includeVideo = ChkIncludeVideo.IsChecked != false;

                // A video track can only be the master if its clips are analyzed,
                // so enable that automatically instead of silently falling back.
                var masterChoice = (CmbMasterTrack.SelectedItem as ComboBoxItem)?.Tag as MasterChoice;
                if (masterChoice != null && masterChoice.IsVideo && !includeVideo)
                {
                    includeVideo = true;
                    ChkIncludeVideo.IsChecked = true;
                    AppendLog("Enabled video clip audio because the selected master is a video track.");
                }

                // Extract timeline tracks with real-time UI logging
                SyncTaskRequest request = TimelineExtractor.ExtractFromVegas(
                    _vegas,
                    selectedOnly,
                    includeVideo,
                    msg => AppendLog(msg),
                    masterChoice?.TrackId);

                if (request.Tracks.Count == 0)
                {
                    AppendLog("No valid audio events found on the timeline to synchronize.");
                    TxtStatus.Text = "No events found.";
                    return;
                }

                // Apply UI options & locate FFmpeg
                if (string.IsNullOrEmpty(_cachedFfmpegPath))
                {
                    _cachedFfmpegPath = await Task.Run(() => FindFfmpegBinary());
                }
                string ffmpegBin = _cachedFfmpegPath;
                request.Options.FfmpegPath = ffmpegBin;
                request.Options.DetectDrift = ChkDetectDrift.IsChecked == true;
                request.Options.SubSampleAccuracy = ChkSubSample.IsChecked == true;
                request.Options.DetectDiscontinuities = ChkDiscontinuity.IsChecked == true;
                request.Options.VoiceActivityFilter = ChkVoiceActivity.IsChecked == true;

                if (masterChoice != null && !string.IsNullOrEmpty(masterChoice.TrackId))
                {
                    request.MasterTrackId = masterChoice.TrackId;
                    AppendLog($"Master anchor locked to {masterChoice.TrackId} (longest take on that track).");
                }
                else
                {
                    AppendLog("Master anchor: Auto-Detect (longest continuous take).");
                }

                int eventCount = 0;
                foreach (var t in request.Tracks) eventCount += t.Events.Count;

                AppendLog($"Queued {eventCount} event(s) across {request.Tracks.Count} track(s). Launching native sync core...");
                AppendLog($"Using extraction binary: '{ffmpegBin}'");
                if (!File.Exists(ffmpegBin))
                {
                    AppendLog("NOTE: FFmpeg was not located. Long takes will be decoded with the slower built-in decoder.");
                }

                // Execute sync on a background thread to keep VEGAS responsive
                SyncManifest manifest = await Task.Run(() =>
                {
                    return _engine.ExecuteSync(
                        request,
                        (progress, message) => PostToUi(() =>
                        {
                            PrgSync.Value = Math.Min(100, Math.Max(0, (int)(progress * 100)));
                            TxtStatus.Text = message;
                        }),
                        () => _cancelRequested);
                });

                if (manifest != null && manifest.Success)
                {
                    AppendLog($"Sync analysis finished in {manifest.ProcessingTimeMs} ms.");
                    AppendLog($"Master reference track: {manifest.MasterTrackId}");

                    if (manifest.Warnings != null)
                    {
                        foreach (var warning in manifest.Warnings)
                        {
                            AppendLog("NOTE: " + warning);
                        }
                    }

                    int alignedCount = 0;
                    foreach (var ev in manifest.Events)
                    {
                        if (ev.IsAligned) alignedCount++;
                        AppendLog($"• [{ev.EventId}] Offset: {ev.DeltaSeconds:+0.000;-0.000;0.000}s, Drift: {ev.DriftPpm:F2} PPM, Rate: {ev.PlaybackRate:F6}, Conf: {(ev.Confidence * 100):F1}% ({ev.Notes})");
                    }

                    if (alignedCount <= 1 && manifest.Events.Count > 1)
                    {
                        TxtStatus.Text = "No secondary events aligned.";
                        AppendLog("Notice: No audio matches were found with sufficient confidence. Verify that the tracks share common audible audio (voices, music, room sound).");
                    }

                    // Apply reconstructed positions to the VEGAS timeline
                    bool preventCrossfades = ChkPreventCrossfades.IsChecked != false;
                    int movedCount = TimelineApplier.ApplyManifest(
                        _vegas, manifest, preventCrossfades, msg => AppendLog(msg));
                    if (movedCount > 0)
                    {
                        TxtStatus.Text = $"Success! {movedCount} event(s) synchronized.";
                        AppendLog($"Timeline updated. Successfully moved {movedCount} event(s).");
                    }
                    else if (alignedCount > 1)
                    {
                        TxtStatus.Text = "Events already in sync.";
                        AppendLog("Notice: Events were already aligned at their current timeline positions.");
                    }
                    else
                    {
                        TxtStatus.Text = "Finished: 0 events moved.";
                    }
                }
                else
                {
                    TxtStatus.Text = "Synchronization failed.";
                    AppendLog("Sync process did not return a successful manifest.");
                }
            }
            catch (OperationCanceledException)
            {
                TxtStatus.Text = "Synchronization cancelled.";
                AppendLog("Cancelled by user. No timeline changes were applied.");
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Error during synchronization";
                AppendLog("ERROR: " + ex);
            }
            finally
            {
                BtnSync.IsEnabled = true;
                BtnRefresh.IsEnabled = true;
                BtnCancel.IsEnabled = false;
                _cancelRequested = false;
            }
        }

        private void PostToUi(Action action)
        {
            try
            {
                if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(action);
            }
            catch
            {
                // The dock may be closing; ignore late progress updates.
            }
        }

        private static string FindFfmpegBinary()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string wingetDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(wingetDir))
                {
                    var matches = Directory.GetFiles(wingetDir, "ffmpeg.exe", SearchOption.AllDirectories);
                    if (matches.Length > 0) return matches[0];
                }

                string[] candidates = {
                    @"C:\ffmpeg\bin\ffmpeg.exe",
                    @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                    @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                    @"C:\Program Files\BorisFX\Vegas Pro 2027\VEGASCapture\ffmpeg.exe",
                    @"C:\Program Files\VEGAS\VEGAS Pro 22.0\VEGASCapture\ffmpeg.exe",
                    @"C:\Program Files\VEGAS\VEGAS Pro 21.0\VEGASCapture\ffmpeg.exe"
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }

                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (string dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch
            {
                // Fallback to standard PATH invocation
            }
            return "ffmpeg";
        }

        private void ClearLog()
        {
            _logLines.Clear();
            TxtLog.Text = string.Empty;
        }

        private void AppendLog(string message)
        {
            if (Dispatcher != null && !Dispatcher.CheckAccess())
            {
                PostToUi(() => AppendLog(message));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string entry = $"[{timestamp}] {message}";

            _logLines.Enqueue(entry);
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
            }

            var sb = new StringBuilder(_logLines.Count * 64);
            bool first = true;
            foreach (string line in _logLines)
            {
                if (!first) sb.AppendLine();
                sb.Append(line);
                first = false;
            }
            TxtLog.Text = sb.ToString();

            LogScroller?.ScrollToEnd();
        }
    }
}
