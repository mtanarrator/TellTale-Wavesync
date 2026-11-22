using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private readonly HashSet<CheckBox> _lockedToggles = new HashSet<CheckBox>();
        private readonly Dictionary<CheckBox, object> _unlockedToolTips = new Dictionary<CheckBox, object>();
        private static readonly Color StatusReadyColor = Color.FromRgb(0x9B, 0xA3, 0xB8);
        private static readonly Color StatusWarningColor = Color.FromRgb(0xE0, 0xA3, 0x4A);
        private static readonly Color StatusErrorColor = Color.FromRgb(0xE0, 0x6C, 0x6C);
        private volatile bool _cancelRequested;
        private bool _suppressInterlock;
        private bool _suppressSettingsSave;
        private bool _engineAvailable = true;
        private bool _initialTrackRefresh = true;
        private UserSettings _settings;
        private string _cachedFfmpegPath;

        public VegasAudioSyncControl() : this(null) { }

        public VegasAudioSyncControl(Vegas vegas)
        {
            InitializeComponent();
            _vegas = vegas;
            _engine = new NativeSyncEngine();
            _settings = UserSettingsStore.Load();

            SetupToggleInterlocks();
            ApplySettingsToUi();
            WireSettingsPersistence();

            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        /// <summary>
        /// Ignore Camera Clock Jumps (DST) is a rigid audio-based mode: camera timestamps are ignored,
        /// playback rate is never changed, and no tracks or split points are produced.
        /// The precision toggles below would therefore silently do nothing, so they
        /// are locked while the mode is active. Clicking a locked toggle explains the
        /// conflict and lets the operator override it (which turns the mode off).
        /// </summary>
        private void SetupToggleInterlocks()
        {
            ChkPreserveOrder.Checked += PreserveOrder_Checked;
            ChkPreserveOrder.Unchecked += PreserveOrder_Unchecked;

            foreach (CheckBox toggle in PreserveConflicts())
            {
                toggle.PreviewMouseLeftButtonDown += LockedToggle_PreviewMouseLeftButtonDown;
                toggle.PreviewKeyDown += LockedToggle_PreviewKeyDown;
                toggle.Checked += LockedToggle_Checked;
            }
        }

        private CheckBox[] PreserveConflicts()
        {
            return new CheckBox[] { ChkDetectDrift, ChkDiscontinuity, ChkPreventCrossfades };
        }

        private static string ToggleTitle(CheckBox toggle)
        {
            var panel = toggle.Content as Panel;
            if (panel != null)
            {
                foreach (UIElement child in panel.Children)
                {
                    var text = child as TextBlock;
                    if (text != null && !string.IsNullOrEmpty(text.Text)) return text.Text;
                }
            }
            return "this option";
        }

        private void PreserveOrder_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressInterlock) return;

            EnforcePreserveInterlocks(true);
            AppendLog("Ignore Camera Clock Jumps (DST) ON: a camera clock jump (including a daylight-saving change) cannot move or reorder clips.");
        }

        /// <summary>
        /// Turns off and locks the options that cannot take effect in rigid-shift
        /// mode. Called when the operator enables the mode and when saved settings
        /// are restored.
        /// </summary>
        private void EnforcePreserveInterlocks(bool log)
        {
            _suppressInterlock = true;
            try
            {
                foreach (CheckBox toggle in PreserveConflicts())
                {
                    if (toggle.IsChecked == true)
                    {
                        toggle.IsChecked = false;
                        if (log)
                        {
                            AppendLog("Ignore Camera Clock Jumps (DST) is ON, so '" + ToggleTitle(toggle) +
                                      "' was switched off (the two modes cannot run together).");
                        }
                    }
                    LockToggle(toggle);
                }
            }
            finally
            {
                _suppressInterlock = false;
            }

            if (TxtPreserveHint != null) TxtPreserveHint.Visibility = Visibility.Visible;
        }

        private void PreserveOrder_Unchecked(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox toggle in PreserveConflicts())
            {
                UnlockToggle(toggle);
            }

            if (TxtPreserveHint != null) TxtPreserveHint.Visibility = Visibility.Collapsed;
        }

        private void LockToggle(CheckBox toggle)
        {
            if (!_lockedToggles.Contains(toggle))
            {
                _lockedToggles.Add(toggle);
                _unlockedToolTips[toggle] = toggle.ToolTip;
            }

            toggle.Opacity = 0.45;
            toggle.ToolTip = "Locked while Ignore Camera Clock Jumps (DST) is ON. Click to review and override.";
        }

        private void UnlockToggle(CheckBox toggle)
        {
            if (_lockedToggles.Remove(toggle))
            {
                object originalToolTip;
                if (_unlockedToolTips.TryGetValue(toggle, out originalToolTip))
                {
                    toggle.ToolTip = originalToolTip;
                    _unlockedToolTips.Remove(toggle);
                }
            }

            toggle.Opacity = 1.0;
        }

        private void LockedToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var toggle = sender as CheckBox;
            if (toggle == null || !_lockedToggles.Contains(toggle)) return;

            e.Handled = true;
            ShowInterlockOverride(toggle);
        }

        private void LockedToggle_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var toggle = sender as CheckBox;
            if (toggle == null || !_lockedToggles.Contains(toggle)) return;

            if (e.Key == Key.Space || e.Key == Key.Enter)
            {
                e.Handled = true;
                ShowInterlockOverride(toggle);
            }
        }

        private void LockedToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Safety net for a non-mouse toggle path (access key, automation, etc.).
            var toggle = sender as CheckBox;
            if (toggle == null || !_lockedToggles.Contains(toggle)) return;

            toggle.IsChecked = false;
            ShowInterlockOverride(toggle);
        }

        private void ShowInterlockOverride(CheckBox toggle)
        {
            string name = ToggleTitle(toggle);
            string message =
                "Ignore Camera Clock Jumps (DST) is ON.\n\n" +
                "In this mode every clip in a source is moved by one rigid, audio-derived offset. " +
                "The camera clock is ignored, playback rate is never changed, overlapping clips are " +
                "reported instead of being spread onto new tracks, and dropped-frame split points are " +
                "not applied.\n\n" +
                "\"" + name + "\" cannot take effect while Ignore Camera Clock Jumps (DST) is ON.\n\n" +
                "Turn OFF Ignore Camera Clock Jumps (DST) and enable \"" + name + "\" anyway?";

            MessageBoxResult answer = MessageBox.Show(
                message,
                "TellTale WaveSync \u2014 Conflicting Options",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            // Unchecking the mode unlocks all dependent toggles; then honor the override.
            ChkPreserveOrder.IsChecked = false;
            toggle.IsChecked = true;
            AppendLog("Override: Ignore Camera Clock Jumps (DST) was turned off so '" + name + "' could be enabled.");
        }

        private void ApplySettingsToUi()
        {
            if (_settings == null) _settings = new UserSettings();

            _suppressSettingsSave = true;
            _suppressInterlock = true;
            try
            {
                ChkDetectDrift.IsChecked = _settings.DetectDrift;
                ChkSubSample.IsChecked = _settings.SubSampleAccuracy;
                ChkDiscontinuity.IsChecked = _settings.DetectDiscontinuities;
                ChkVoiceActivity.IsChecked = _settings.VoiceActivityFilter;
                ChkSelectedOnly.IsChecked = _settings.SelectedOnly;
                ChkIncludeVideo.IsChecked = _settings.IncludeVideo;
                ChkPreventCrossfades.IsChecked = _settings.PreventCrossfades;
                ChkPreserveOrder.IsChecked = _settings.PreserveTimelineOrder;
            }
            finally
            {
                _suppressInterlock = false;
                _suppressSettingsSave = false;
            }

            // A saved DST state may predate the interlock, or the operator may have
            // edited the JSON by hand. Re-apply the rule without logging a mode change.
            if (ChkPreserveOrder.IsChecked == true)
            {
                EnforcePreserveInterlocks(false);
            }
        }

        private void WireSettingsPersistence()
        {
            CheckBox[] toggles =
            {
                ChkDetectDrift,
                ChkSubSample,
                ChkDiscontinuity,
                ChkVoiceActivity,
                ChkSelectedOnly,
                ChkIncludeVideo,
                ChkPreventCrossfades,
                ChkPreserveOrder
            };

            foreach (CheckBox toggle in toggles)
            {
                toggle.Checked += SettingToggle_Changed;
                toggle.Unchecked += SettingToggle_Changed;
            }

            CmbMasterTrack.SelectionChanged += MasterTrack_SelectionChanged;
        }

        private void SettingToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingsSave || _suppressInterlock) return;
            SaveSettings();
        }

        private void MasterTrack_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSettingsSave) return;
            SaveSettings();
        }

        private void SaveSettings()
        {
            if (_settings == null) _settings = new UserSettings();

            _settings.PreserveTimelineOrder = ChkPreserveOrder.IsChecked == true;
            _settings.DetectDrift = ChkDetectDrift.IsChecked == true;
            _settings.SubSampleAccuracy = ChkSubSample.IsChecked == true;
            _settings.DetectDiscontinuities = ChkDiscontinuity.IsChecked == true;
            _settings.VoiceActivityFilter = ChkVoiceActivity.IsChecked == true;
            _settings.SelectedOnly = ChkSelectedOnly.IsChecked == true;
            _settings.IncludeVideo = ChkIncludeVideo.IsChecked == true;
            _settings.PreventCrossfades = ChkPreventCrossfades.IsChecked == true;

            var choice = (CmbMasterTrack.SelectedItem as ComboBoxItem)?.Tag as MasterChoice;
            _settings.MasterTrackId = choice != null ? choice.TrackId : null;
            _settings.MasterTrackName = choice != null ? choice.Name : null;
            _settings.MasterTrackIsVideo = choice != null && choice.IsVideo;

            UserSettingsStore.Save(_settings);
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            // Closing the dock during a long analysis should stop the work.
            _cancelRequested = true;
            SaveSettings();
        }

        private void SetStatus(string text, Color color)
        {
            TxtStatus.Text = text;
            TxtStatus.Foreground = new SolidColorBrush(color);
        }

        /// <summary>
        /// "telltale_sync_core v0.2.0 (SIMD-enabled Rust Native Engine)" -> "v0.2.0"
        /// </summary>
        private static string ShortEngineVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "unknown";

            int marker = version.IndexOf(" v", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return version;

            int start = marker + 1;
            int end = version.IndexOf(' ', start);
            return end > start ? version.Substring(start, end - start) : version.Substring(start);
        }

        private async void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string ver = _engine.GetEngineVersion();
                string extensionVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

                if (string.IsNullOrEmpty(ver)
                    || ver.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _engineAvailable = false;
                    SetStatus("Native engine unavailable — reinstall TellTale WaveSync", StatusErrorColor);
                    AppendLog("Failed to load native engine: the version query returned '" + (ver ?? "null") + "'.");
                    BtnSync.IsEnabled = false;
                }
                else if (!NativeSyncEngine.IsCompatibleVersion(ver))
                {
                    SetStatus("Engine " + ShortEngineVersion(ver) + " mismatch — expected v0.2.x", StatusWarningColor);
                    AppendLog(
                        "WARNING: native engine reports '" + ver + "'; this extension expects " +
                        NativeSyncEngine.ExpectedVersionPrefix + "x. Reinstall the matching TellTale WaveSync package.");
                }
                else
                {
                    SetStatus("Ready — ext " + extensionVersion + " / engine " + ShortEngineVersion(ver), StatusReadyColor);
                    AppendLog("Native engine: " + ver + " (extension " + extensionVersion + ").");
                }
            }
            catch (Exception ex)
            {
                _engineAvailable = false;
                SetStatus("Native engine failed to load — reinstall TellTale WaveSync", StatusErrorColor);
                AppendLog("Failed to load native engine: " + ex.Message);
                BtnSync.IsEnabled = false;
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
            public string Name;
            public bool IsVideo;
        }

        private bool MatchesSavedMaster(MasterChoice choice)
        {
            if (_settings == null || choice == null) return false;
            if (!string.Equals(choice.TrackId, _settings.MasterTrackId, StringComparison.Ordinal)) return false;
            if (!string.Equals(choice.Name ?? string.Empty, _settings.MasterTrackName ?? string.Empty, StringComparison.Ordinal)) return false;
            return choice.IsVideo == _settings.MasterTrackIsVideo;
        }

        private void RefreshTimelineTracks()
        {
            if (_vegas == null || _vegas.Project == null) return;

            bool restoreSavedMaster = _initialTrackRefresh;
            _initialTrackRefresh = false;

            // Preserve the operator's choice across refreshes.
            string previousTrackId = null;
            var previousChoice = (CmbMasterTrack.SelectedItem as ComboBoxItem)?.Tag as MasterChoice;
            if (previousChoice != null)
            {
                previousTrackId = previousChoice.TrackId;
            }
            else if (restoreSavedMaster && _settings != null)
            {
                // First population of this session: restore the saved master anchor.
                previousTrackId = _settings.MasterTrackId;
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
                    Name = name,
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

                if (previousTrackId != null
                    && previousTrackId == choice.TrackId
                    && (!restoreSavedMaster || MatchesSavedMaster(choice)))
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

            if (!_engineAvailable)
            {
                MessageBox.Show(
                    "The native sync engine is not available. Reinstall TellTale WaveSync, then restart VEGAS Pro.",
                    "TellTale WaveSync",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _cancelRequested = false;
            BtnSync.IsEnabled = false;
            BtnRefresh.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            PrgSync.Value = 0;
            SetStatus("Analyzing timeline...", StatusReadyColor);
            ClearLog();

            try
            {
                bool selectedOnly = ChkSelectedOnly.IsChecked == true;
                bool includeVideo = ChkIncludeVideo.IsChecked != false;
                bool preserveOrder = ChkPreserveOrder.IsChecked == true;

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
                    SetStatus("No events found.", StatusWarningColor);
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
                request.Options.PreserveTimelineOrder = preserveOrder;

                if (preserveOrder)
                {
                    AppendLog("Ignore Camera Clock Jumps (DST) enabled: recorded timestamps are ignored for placement, so camera clock jumps cannot move or reorder clips; no tracks will be added.");
                }

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
                        SetStatus("No secondary events aligned.", StatusWarningColor);
                        AppendLog("Notice: No audio matches were found with sufficient confidence. Verify that the tracks share common audible audio (voices, music, room sound).");
                    }

                    // Cancellation is honored until the moment of apply. Once the
                    // UndoBlock starts, the pass is committed as a unit; disable the
                    // Stop button rather than pretending it can interrupt the move.
                    if (_cancelRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    SetStatus("Applying changes...", StatusReadyColor);
                    BtnCancel.IsEnabled = false;

                    // Apply reconstructed positions to the VEGAS timeline
                    bool preventCrossfades = ChkPreventCrossfades.IsChecked != false;
                    TimelineApplier.ApplyResult apply = TimelineApplier.ApplyManifest(
                        _vegas,
                        manifest,
                        request,
                        preventCrossfades,
                        preserveOrder,
                        msg => AppendLog(msg));

                    int unalignedCount = manifest.Events.Count - alignedCount;
                    if (apply.Skipped > 0)
                    {
                        SetStatus($"Completed with warnings: {apply.Moved} moved, {apply.Skipped} skipped.", StatusWarningColor);
                        AppendLog($"Timeline updated with warnings: {apply.Moved} moved, {apply.Skipped} skipped. See the warnings above for the affected events.");
                    }
                    else if (apply.Moved > 0)
                    {
                        SetStatus(
                            unalignedCount > 0
                                ? $"Success! {apply.Moved} moved; {unalignedCount} left unaligned."
                                : $"Success! {apply.Moved} event(s) synchronized.",
                            unalignedCount > 0 ? StatusWarningColor : StatusReadyColor);
                        AppendLog($"Timeline updated. Successfully moved {apply.Moved} event(s).");
                    }
                    else if (alignedCount > 1)
                    {
                        SetStatus(
                            unalignedCount > 0
                                ? $"Events already in sync; {unalignedCount} left unaligned."
                                : "Events already in sync.",
                            unalignedCount > 0 ? StatusWarningColor : StatusReadyColor);
                        AppendLog("Notice: Events were already aligned at their current timeline positions.");
                    }
                    else
                    {
                        SetStatus("Finished: 0 events moved.", StatusReadyColor);
                    }
                }
                else
                {
                    SetStatus("Synchronization failed.", StatusErrorColor);
                    AppendLog("Sync process did not return a successful manifest.");
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("Synchronization cancelled.", StatusReadyColor);
                AppendLog("Cancelled by user. No timeline changes were applied.");
            }
            catch (Exception ex)
            {
                SetStatus("Error during synchronization", StatusErrorColor);
                AppendLog("ERROR: " + ex);
            }
            finally
            {
                BtnSync.IsEnabled = _engineAvailable;
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
