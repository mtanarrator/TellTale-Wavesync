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
using System.Windows.Threading;
using ScriptPortal.Vegas;
using VegasAudioSync.Interop;
using VegasAudioSync.Models;
using VegasAudioSync.Services;

namespace VegasAudioSync.UI
{
    public partial class VegasAudioSyncControl : UserControl
    {
        private const int MaxLogLines = 2000;
        private const string DstDisplayName = "Force audio-only placement (DST)";

        private readonly Vegas _vegas;
        private readonly NativeSyncEngine _engine;
        private readonly Queue<string> _logLines = new Queue<string>();
        private readonly Queue<string> _pendingLogLines = new Queue<string>();
        private readonly DispatcherTimer _logFlushTimer;
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
        private bool _hostEventsWired;
        private bool _syncInProgress;
        private bool _logHasContent;
        private bool _logWarningsOnly;
        private int _projectGeneration;
        private UserSettings _settings;
        private string _cachedFfmpegPath;

        public VegasAudioSyncControl() : this(null) { }

        public VegasAudioSyncControl(Vegas vegas)
        {
            InitializeComponent();
            _vegas = vegas;
            _engine = new NativeSyncEngine();
            _settings = UserSettingsStore.Load();

            // One shared timer for the whole control: AppendLog only queues text and
            // the buffer is flushed to the UI a few times per second.
            _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _logFlushTimer.Tick += (s, e) => FlushLog();

            SetupToggleInterlocks();
            ApplySettingsToUi();
            WireSettingsPersistence();

            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        /// <summary>
        /// Force audio-only placement (DST) is a rigid audio-based mode: camera timestamps are ignored,
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
            AppendLog(DstDisplayName + " ON: a camera clock jump (including a daylight-saving change) cannot move or reorder clips.");
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
                            AppendLog(DstDisplayName + " is ON, so '" + ToggleTitle(toggle) +
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
            toggle.ToolTip = "Locked while " + DstDisplayName + " is ON. Click to review and override.";
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

            // Presets and settings restore write toggles under suppression; they must
            // not pop the modal or be forced back to OFF.
            if (_suppressInterlock) return;

            toggle.IsChecked = false;
            ShowInterlockOverride(toggle);
        }

        private void ShowInterlockOverride(CheckBox toggle)
        {
            string name = ToggleTitle(toggle);
            string message =
                DstDisplayName + " is ON.\n\n" +
                "In this mode every clip in a source is moved by one rigid, audio-derived offset. " +
                "The camera clock is ignored, playback rate is never changed, overlapping clips are " +
                "reported instead of being spread onto new tracks, and dropped-frame split points are " +
                "not applied.\n\n" +
                "\"" + name + "\" cannot take effect while " + DstDisplayName + " is ON.\n\n" +
                "Turn OFF " + DstDisplayName + " and enable \"" + name + "\" anyway?";

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
            AppendLog("Override: " + DstDisplayName + " was turned off so '" + name + "' could be enabled.");
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
                ChkGlobalMatching.IsChecked = _settings.GlobalMatching;
                ChkPreserveOrder.IsChecked = _settings.PreserveTimelineOrder;
                SelectComboByTag(CmbTimeSource, _settings.TimeSource);
                SelectComboByTag(CmbClipOrder, _settings.ClipOrder);
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
                ChkGlobalMatching,
                ChkPreserveOrder
            };

            foreach (CheckBox toggle in toggles)
            {
                toggle.Checked += SettingToggle_Changed;
                toggle.Unchecked += SettingToggle_Changed;
            }

            CmbMasterTrack.SelectionChanged += MasterTrack_SelectionChanged;
            CmbTimeSource.SelectionChanged += ClockChoice_SelectionChanged;
            CmbClipOrder.SelectionChanged += ClockChoice_SelectionChanged;
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

        private void ClockChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSettingsSave) return;
            SaveSettings();
        }

        /// <summary>
        /// Reads the Tag string from a clock/order ComboBox, falling back to "auto".
        /// </summary>
        private static string SelectedTagOrAuto(ComboBox combo)
        {
            string tag = (combo?.SelectedItem as ComboBoxItem)?.Tag as string;
            return string.IsNullOrEmpty(tag) ? "auto" : tag;
        }

        private string SelectedTimeSource()
        {
            return SelectedTagOrAuto(CmbTimeSource);
        }

        private string SelectedClipOrder()
        {
            return SelectedTagOrAuto(CmbClipOrder);
        }

        /// <summary>
        /// Selects the item whose Tag matches <paramref name="tag"/> (case-insensitive);
        /// unknown or empty values fall back to the first item (Auto).
        /// </summary>
        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            if (combo == null) return;

            string wanted = string.IsNullOrEmpty(tag) ? "auto" : tag;
            foreach (object entry in combo.Items)
            {
                var item = entry as ComboBoxItem;
                if (item != null &&
                    string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
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
            _settings.GlobalMatching = ChkGlobalMatching.IsChecked == true;

            var choice = (CmbMasterTrack.SelectedItem as ComboBoxItem)?.Tag as MasterChoice;
            _settings.MasterTrackId = choice != null ? choice.TrackId : null;
            _settings.MasterTrackName = choice != null ? choice.Name : null;
            _settings.MasterTrackIsVideo = choice != null && choice.IsVideo;

            _settings.TimeSource = SelectedTimeSource();
            _settings.ClipOrder = SelectedClipOrder();

            UserSettingsStore.Save(_settings);
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            // Closing the dock during a long analysis should stop the work.
            _cancelRequested = true;
            _logFlushTimer?.Stop();
            UnwireHostEvents();
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

        /// <summary>
        /// Raised when the VEGAS project is replaced or closed while an analysis is
        /// running. Derives from <see cref="OperationCanceledException"/> so the
        /// existing cancel handling stays in one place.
        /// </summary>
        private sealed class ProjectChangedException : OperationCanceledException
        {
            public ProjectChangedException(string message) : base(message) { }
        }

        /// <summary>
        /// Hooks the host lifecycle events. Called from <c>Loaded</c>; the dock can be
        /// closed and reopened, so double subscription is guarded by a flag.
        /// </summary>
        private void WireHostEvents()
        {
            if (_vegas == null || _hostEventsWired) return;

            // Mark wired first: if one subscription throws, the flags still reflect
            // that a partial hook exists, so a retry cannot double-subscribe.
            _hostEventsWired = true;
            try { _vegas.ProjectOpened += Vegas_ProjectOpened; } catch { }
            try { _vegas.ProjectClosed += Vegas_ProjectClosed; } catch { }
            try { _vegas.TrackCountChanged += Vegas_TrackCountChanged; } catch { }
        }

        private void UnwireHostEvents()
        {
            if (_vegas == null || !_hostEventsWired) return;
            _hostEventsWired = false;

            try
            {
                _vegas.ProjectOpened -= Vegas_ProjectOpened;
                _vegas.ProjectClosed -= Vegas_ProjectClosed;
                _vegas.TrackCountChanged -= Vegas_TrackCountChanged;
            }
            catch
            {
                // The host may already be tearing down.
            }
        }

        private void Vegas_ProjectOpened(object sender, EventArgs e)
        {
            // Any analysis started against the previous project must not be applied.
            _projectGeneration++;
            _cancelRequested = false;

            ResetProjectState();
            ApplySettingsToUi();
            RefreshTimelineTracks();

            if (_engineAvailable) SetStatus("Project opened — ready.", StatusReadyColor);
            AppendLog("Project opened: track list and master anchor refreshed.");
        }

        private void Vegas_ProjectClosed(object sender, EventArgs e)
        {
            _projectGeneration++;
            _cancelRequested = true;

            ResetProjectState();
            PrgSync.Value = 0;
            SetStatus("Project closed.", StatusReadyColor);
            AppendLog("Project closed: pending results discarded.");
        }

        private void Vegas_TrackCountChanged(object sender, EventArgs e)
        {
            // ApplyManifest adds tracks itself; re-enumerating the project mid-apply
            // would churn the combo box for no benefit, so wait until the pass ends.
            if (_syncInProgress) return;
            RefreshTimelineTracks();
        }

        /// <summary>
        /// Drops per-project cached state: the master combo contents and the
        /// "restore the saved anchor" one-shot flag. The saved settings themselves
        /// are kept so the next project reopens with the same anchor and toggles.
        /// </summary>
        private void ResetProjectState()
        {
            _initialTrackRefresh = true;

            _suppressSettingsSave = true;
            try
            {
                if (CmbMasterTrack != null) CmbMasterTrack.Items.Clear();
            }
            finally
            {
                _suppressSettingsSave = false;
            }
        }

        /// <summary>
        /// The manifest (and its TrackEvent COM references) belongs to the project it
        /// was extracted from. The generation counter is the contract; COM object
        /// identity is never compared.
        /// </summary>
        private void ThrowIfProjectChanged(int generation)
        {
            if (generation != _projectGeneration || _vegas == null || _vegas.Project == null)
            {
                throw new ProjectChangedException("Project changed during analysis; results discarded.");
            }
        }

        private async void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            WireHostEvents();

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
                Content = "Auto (recommended)",
                Foreground = System.Windows.Media.Brushes.Black,
                Tag = null,
                ToolTip = "Best for most projects: automatically picks the longest continuous take as the reference."
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

        private void PresetMulticam_Click(object sender, RoutedEventArgs e)
        {
            ApplyPreset(true, true, false, false, false, false, "Multicam", false, false);
        }

        private void PresetLongRecording_Click(object sender, RoutedEventArgs e)
        {
            ApplyPreset(true, true, true, false, true, false, "Long recording", false, false);
        }

        private void PresetDamaged_Click(object sender, RoutedEventArgs e)
        {
            ApplyPreset(true, true, false, true, true, false, "Damaged footage", false, false);
        }

        private void ResetRecommended_Click(object sender, RoutedEventArgs e)
        {
            ApplyPreset(true, true, false, false, false, false, "Reset defaults", true, true);
        }

        /// <summary>
        /// Applies a known-good toggle matrix in one pass: no per-toggle settings
        /// writes, no DST confirmation dialog, one save and one log line. The
        /// global matching toggle is not part of the matrix; only Reset defaults
        /// turns it back on, every other preset leaves the operator's choice alone.
        /// </summary>
        private void ApplyPreset(bool includeVideo, bool preventCrossfades, bool drift, bool discontinuity,
            bool subSample, bool voiceActivity, string label, bool resetSelectedOnly, bool resetGlobalMatching)
        {
            _suppressInterlock = true;
            _suppressSettingsSave = true;
            try
            {
                // Clear DST first: while it is ON, the dependent toggles are locked and
                // a write to them would trigger the conflict dialog and be forced back.
                // PreserveOrder_Unchecked also unlocks the dependent toggles.
                ChkPreserveOrder.IsChecked = false;
                ChkIncludeVideo.IsChecked = includeVideo;
                ChkPreventCrossfades.IsChecked = preventCrossfades;
                ChkDetectDrift.IsChecked = drift;
                ChkDiscontinuity.IsChecked = discontinuity;
                ChkSubSample.IsChecked = subSample;
                ChkVoiceActivity.IsChecked = voiceActivity;
                if (resetSelectedOnly) ChkSelectedOnly.IsChecked = false;
                if (resetGlobalMatching) ChkGlobalMatching.IsChecked = true;
                CmbMasterTrack.SelectedIndex = 0; // Auto (recommended)
            }
            finally
            {
                _suppressInterlock = false;
                _suppressSettingsSave = false;
            }

            SaveSettings();
            AppendLog("Preset: " + label);
            SetStatus("Preset applied: " + label, StatusReadyColor);
        }

        /// <summary>
        /// Groups timeline clips by recording device and builds per-device camera or
        /// recorder tracks. The scan is read-only; only an Apply in the preview dialog
        /// changes the timeline, and that pass is one undo step.
        /// </summary>
        private void BtnOrganizeDevices_Click(object sender, RoutedEventArgs e)
        {
            if (_vegas == null || _vegas.Project == null)
            {
                MessageBox.Show("Please open a VEGAS Pro project first.", "TellTale WaveSync", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_syncInProgress)
            {
                SetStatus("Finish the current sync before organizing tracks.", StatusWarningColor);
                AppendLog("Finish the current sync before organizing tracks.");
                return;
            }

            string ffprobe = DeviceOrganizer.FindFfprobe(_cachedFfmpegPath);

            _syncInProgress = true;
            BtnOrganizeDevices.IsEnabled = false;
            BtnSync.IsEnabled = false;
            BtnRefresh.IsEnabled = false;
            SetStatus("Scanning project media…", StatusReadyColor);
            ResultSummaryCard.Visibility = Visibility.Collapsed;
            TxtResultSummary.Text = string.Empty;

            try
            {
                // VEGAS COM must be read on the UI thread.
                DeviceScanResult scan = DeviceOrganizer.Scan(_vegas, ffprobe, msg => AppendLog(msg));

                if (scan == null || scan.Groups == null || scan.Groups.Count == 0)
                {
                    SetStatus("No camera or recorder media found.", StatusWarningColor);
                    AppendLog("No camera or recorder media found on the timeline.");
                    return;
                }

                AppendLog($"Device scan found {scan.Groups.Count} device group(s).");
                if (scan.Warnings != null && scan.Warnings.Count > 0)
                {
                    AppendLog($"Scan warnings: {scan.Warnings.Count}; see the notes above.");
                }

                var dialog = new DeviceOrganizerDialog(scan) { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() != true)
                {
                    SetStatus("Device organization cancelled.", StatusReadyColor);
                    return;
                }

                DeviceScanResult plan = dialog.Result;
                DeviceApplyResult result;

                try
                {
                    // Apply logs its own warnings through the callback below.
                    result = DeviceOrganizer.Apply(
                        _vegas,
                        plan,
                        msg => AppendLog(msg));
                }
                catch (Exception ex)
                {
                    ResultSummaryCard.Visibility = Visibility.Collapsed;
                    SetStatus("Could not organize tracks.", StatusErrorColor);
                    AppendLog("ERROR: " + ex);
                    return;
                }

                ShowDeviceResultSummary(result);
                SetStatus(
                    result.GroupsSkipped > 0
                        ? $"Track organization finished with {result.GroupsSkipped} skipped device(s)."
                        : "Track organization complete.",
                    result.GroupsSkipped > 0 ? StatusWarningColor : StatusReadyColor);

                // Apply may have created camera/recorder tracks; show them now.
                RefreshTimelineTracks();
            }
            catch (Exception ex)
            {
                ResultSummaryCard.Visibility = Visibility.Collapsed;
                SetStatus("Could not organize tracks.", StatusErrorColor);
                AppendLog("ERROR: " + ex);
            }
            finally
            {
                _syncInProgress = false;
                BtnSync.IsEnabled = _engineAvailable;
                BtnRefresh.IsEnabled = true;
                BtnOrganizeDevices.IsEnabled = true;
            }
        }

        private void ShowDeviceResultSummary(DeviceApplyResult result)
        {
            TxtResultSummary.Text =
                $"Devices applied {result.GroupsApplied} · Skipped {result.GroupsSkipped} · Tracks created {result.TracksCreated} · Events moved {result.EventsMoved}"
                + Environment.NewLine
                + "Press Ctrl+Z to undo the whole pass.";
            ResultSummaryCard.Visibility = Visibility.Visible;
            try { ResultSummaryCard.BringIntoView(); } catch { }
        }

        /// <summary>
        /// Counts the placement outcomes the engine reported in the manifest notes.
        /// Notes are free text, so the counts only reflect what the engine actually
        /// wrote; nothing is inferred from the UI toggle, which means a silent
        /// fallback to the legacy pipeline is still reported as the legacy path.
        /// Audio-aligned, clock-placed and unaligned are mutually exclusive buckets:
        /// clock placement counts only when the event is aligned, and unaligned is
        /// exactly !IsAligned.
        /// </summary>
        private sealed class SyncPathSummary
        {
            public bool GlobalPath { get; set; }
            public int GlobalAudioAligned { get; set; }
            public int ClockPlaced { get; set; }
            public int Broll { get; set; }
            public int LegacyVerified { get; set; }
            public int MasterAnchors { get; set; }
            public int Unaligned { get; set; }

            public string ToSummaryLine()
            {
                if (GlobalPath)
                {
                    return $"Path: Global matching — audio-aligned {GlobalAudioAligned} · clock-placed {ClockPlaced} · B-roll {Broll} · unaligned {Unaligned}";
                }

                return $"Path: Legacy master anchor — audio-verified {LegacyVerified} · master anchor {MasterAnchors} · unaligned {Unaligned}";
            }
        }

        private static SyncPathSummary SummarizeManifest(SyncManifest manifest)
        {
            var summary = new SyncPathSummary();
            if (manifest == null) return summary;

            if (manifest.Events != null)
            {
                foreach (EventSyncResult ev in manifest.Events)
                {
                    if (ev == null) continue;
                    string notes = ev.Notes ?? string.Empty;

                    // B-roll is informational and can be aligned or not.
                    if (NoteContains(notes, "b-roll")) summary.Broll++;

                    if (!ev.IsAligned)
                    {
                        // An unaligned clip may still mention clock placement in its
                        // note; it belongs in the unaligned bucket only, so the
                        // audio/clock/unaligned buckets never count it twice.
                        summary.Unaligned++;
                        continue;
                    }

                    if (NoteContains(notes, "global audio graph"))
                    {
                        summary.GlobalAudioAligned++;
                    }
                    else if (NoteContains(notes, "clock-placed") || NoteContains(notes, "sequence"))
                    {
                        summary.ClockPlaced++;
                    }
                    else if (NoteContains(notes, "Aligned via "))
                    {
                        summary.LegacyVerified++;
                    }
                    else if (NoteContains(notes, "Master reference"))
                    {
                        summary.MasterAnchors++;
                    }
                }
            }

            // Fail closed: a warning that reports a fallback, guard or skip forces
            // the legacy label, even when the attempt left "Global matching: ..."
            // warnings in the manifest. Only then may note markers or a genuine
            // global completion warning select the global label.
            if (HasFallbackWarning(manifest.Warnings))
            {
                summary.GlobalPath = false;
                return summary;
            }

            summary.GlobalPath = summary.GlobalAudioAligned > 0
                || summary.ClockPlaced > 0
                || HasGlobalCompletionWarning(manifest.Warnings);
            return summary;
        }

        private static bool HasFallbackWarning(List<string> warnings)
        {
            if (warnings == null) return false;
            foreach (string line in warnings)
            {
                if (NoteContains(line, "falling back")
                    || NoteContains(line, "fallback")
                    || NoteContains(line, "fell back")
                    || NoteContains(line, "not applicable")
                    || NoteContains(line, "no usable audio edge")
                    || NoteContains(line, "exceeds the global limits")
                    || NoteContains(line, "fewer than two events")
                    || NoteContains(line, "falling back to the legacy pipeline"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasGlobalCompletionWarning(List<string> warnings)
        {
            if (warnings == null) return false;
            foreach (string line in warnings)
            {
                if (NoteContains(line, "global matching"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool NoteContains(string text, string value)
        {
            return !string.IsNullOrEmpty(text)
                && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ShowResultSummary(TimelineApplier.ApplyResult apply, int unalignedCount, string masterLabel,
            SyncPathSummary path)
        {
            // Global matching pins the selected master only as a gauge; it is not
            // an anchor, so do not show the master track name as the reference.
            string reference = path.GlobalPath
                ? "Reference: Global graph"
                : "Reference " + masterLabel;
            TxtResultSummary.Text =
                $"Moved {apply.Moved} · Skipped {apply.Skipped} · Unaligned {unalignedCount} · New Sync Lanes {apply.LanesCreated} · {reference}" +
                Environment.NewLine +
                path.ToSummaryLine() +
                Environment.NewLine +
                "Press Ctrl+Z to undo the whole sync.";
            ResultSummaryCard.Visibility = Visibility.Visible;
            try { ResultSummaryCard.BringIntoView(); } catch { }
        }

        /// <summary>
        /// Best human-readable name for the reference track: the operator's explicit
        /// choice, otherwise the track the engine actually used, otherwise "Auto".
        /// </summary>
        private string ResolveMasterLabel(SyncManifest manifest, MasterChoice choice)
        {
            if (choice != null && !string.IsNullOrEmpty(choice.Name)) return choice.Name;

            string id = manifest != null ? manifest.MasterTrackId : null;
            if (!string.IsNullOrEmpty(id)
                && id.StartsWith("Track_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(id.Substring("Track_".Length), out int index))
            {
                try
                {
                    foreach (Track track in _vegas.Project.Tracks)
                    {
                        if (track.Index == index)
                        {
                            return string.IsNullOrEmpty(track.Name) ? id : track.Name;
                        }
                    }
                }
                catch
                {
                    // Fall through to the raw id.
                }
            }

            return string.IsNullOrEmpty(id) ? "Auto" : id;
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
            // Any project open/close during the await below bumps this counter; the
            // manifest and its TrackEvent COM references belong to the old project.
            int analysisGeneration = _projectGeneration;
            _syncInProgress = true;
            BtnSync.IsEnabled = false;
            BtnRefresh.IsEnabled = false;
            BtnOrganizeDevices.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            PrgSync.Value = 0;
            SetStatus("Analyzing timeline...", StatusReadyColor);
            ClearLog();
            ResultSummaryCard.Visibility = Visibility.Collapsed;
            TxtResultSummary.Text = string.Empty;

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

                // Resolve FFmpeg/ffprobe before extraction: the extractor uses
                // ffprobe for container timecode and creation-time metadata.
                if (string.IsNullOrEmpty(_cachedFfmpegPath))
                {
                    _cachedFfmpegPath = await Task.Run(() => FindFfmpegBinary());
                }
                string ffmpegBin = _cachedFfmpegPath;

                // Extract timeline tracks with real-time UI logging
                SyncTaskRequest request = TimelineExtractor.ExtractFromVegas(
                    _vegas,
                    selectedOnly,
                    includeVideo,
                    msg => AppendLog(msg),
                    masterChoice?.TrackId,
                    _cachedFfmpegPath);

                if (request.Tracks.Count == 0)
                {
                    AppendLog("No valid audio events found on the timeline to synchronize.");
                    SetStatus("No events found.", StatusWarningColor);
                    return;
                }

                // Apply UI options
                request.Options.FfmpegPath = ffmpegBin;
                request.Options.DetectDrift = ChkDetectDrift.IsChecked == true;
                request.Options.SubSampleAccuracy = ChkSubSample.IsChecked == true;
                request.Options.DetectDiscontinuities = ChkDiscontinuity.IsChecked == true;
                request.Options.VoiceActivityFilter = ChkVoiceActivity.IsChecked == true;
                request.Options.PreserveTimelineOrder = preserveOrder;
                request.Options.GlobalMatching = ChkGlobalMatching.IsChecked == true;
                request.Options.TimeSource = SelectedTimeSource();
                request.Options.ClipOrder = SelectedClipOrder();

                if (preserveOrder)
                {
                    AppendLog(DstDisplayName + " enabled: recorded timestamps are ignored for placement, so camera clock jumps cannot move or reorder clips; no tracks will be added.");
                }

                if (masterChoice != null && !string.IsNullOrEmpty(masterChoice.TrackId))
                {
                    request.MasterTrackId = masterChoice.TrackId;
                    AppendLog($"Master anchor locked to {masterChoice.TrackId} (longest take on that track).");
                }
                else
                {
                    AppendLog("Master anchor: Auto (recommended) — longest continuous take.");
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
                    ThrowIfProjectChanged(analysisGeneration);

                    AppendLog($"Sync analysis finished in {manifest.ProcessingTimeMs} ms.");
                    SyncPathSummary pathSummary = SummarizeManifest(manifest);
                    if (pathSummary.GlobalPath)
                    {
                        AppendLog("Reference: Global graph (the selected master only pins the gauge).");
                    }
                    else
                    {
                        AppendLog($"Master reference track: {manifest.MasterTrackId}");
                    }

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

                    AppendLog(pathSummary.ToSummaryLine());

                    // Cancellation is honored until the moment of apply. Once the
                    // UndoBlock starts, the pass is committed as a unit; disable the
                    // Stop button rather than pretending it can interrupt the move.
                    if (_cancelRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    SetStatus("Applying changes...", StatusReadyColor);
                    BtnCancel.IsEnabled = false;

                    // Last free point to abandon: once the UndoBlock starts the pass
                    // is committed as a unit.
                    ThrowIfProjectChanged(analysisGeneration);

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

                    string masterLabel = ResolveMasterLabel(manifest, masterChoice);
                    ShowResultSummary(apply, unalignedCount, masterLabel, pathSummary);

                    // ApplyManifest may have created Sync Lane tracks; show them now
                    // instead of waiting for the next manual Refresh.
                    RefreshTimelineTracks();
                }
                else
                {
                    SetStatus("Synchronization failed.", StatusErrorColor);
                    AppendLog("Sync process did not return a successful manifest.");
                }
            }
            catch (ProjectChangedException)
            {
                ResultSummaryCard.Visibility = Visibility.Collapsed;
                SetStatus("Project changed; results discarded.", StatusWarningColor);
                AppendLog("Project changed during analysis; no timeline changes were applied.");
            }
            catch (OperationCanceledException)
            {
                ResultSummaryCard.Visibility = Visibility.Collapsed;
                SetStatus("Synchronization cancelled.", StatusReadyColor);
                AppendLog("Cancelled by user. No timeline changes were applied.");
            }
            catch (Exception ex)
            {
                ResultSummaryCard.Visibility = Visibility.Collapsed;
                SetStatus("Error during synchronization", StatusErrorColor);
                AppendLog("ERROR: " + ex);
            }
            finally
            {
                _syncInProgress = false;
                BtnSync.IsEnabled = _engineAvailable;
                BtnRefresh.IsEnabled = true;
                BtnOrganizeDevices.IsEnabled = true;
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
            _pendingLogLines.Clear();
            _logLines.Clear();
            _logFlushTimer?.Stop();
            _logHasContent = false;
            TxtLog.Text = "Analyzing timeline…";
        }

        private void ChkLogWarningsOnly_Checked(object sender, RoutedEventArgs e)
        {
            ApplyLogFilter();
        }

        private void ChkLogWarningsOnly_Unchecked(object sender, RoutedEventArgs e)
        {
            ApplyLogFilter();
        }

        private static bool IsDiagnosticLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;

            return line.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("NOTE", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("SKIPPED", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("OVERRIDE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Visible text for the whole history under the active filter. The history
        /// itself is never modified by filtering.
        /// </summary>
        private string BuildVisibleLog()
        {
            var sb = new StringBuilder(_logLines.Count * 64);
            foreach (string line in _logLines)
            {
                if (_logWarningsOnly && !IsDiagnosticLine(line)) continue;
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        private void ApplyLogFilter()
        {
            if (TxtLog == null || ChkLogWarningsOnly == null) return;

            _logWarningsOnly = ChkLogWarningsOnly.IsChecked == true;

            string text = BuildVisibleLog();
            if (text.Length == 0)
            {
                _logHasContent = false;
                TxtLog.Text = _logWarningsOnly
                    ? "No warnings or errors so far."
                    : "Ready. Click 'Sync Timeline' to analyze and align multi-track audio.";
                return;
            }

            _logHasContent = true;
            TxtLog.Text = text;
            TxtLog.CaretIndex = TxtLog.Text.Length;
            TxtLog.ScrollToEnd();
        }

        private void AppendLog(string message)
        {
            if (Dispatcher != null && !Dispatcher.CheckAccess())
            {
                PostToUi(() => AppendLog(message));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _pendingLogLines.Enqueue($"[{timestamp}] {message}");

            // Bound the pending queue too: a burst (1,000-clip apply) must not grow
            // memory between flushes.
            while (_pendingLogLines.Count > MaxLogLines)
            {
                _pendingLogLines.Dequeue();
            }

            if (_logFlushTimer != null && !_logFlushTimer.IsEnabled)
            {
                _logFlushTimer.Start();
            }
        }

        /// <summary>
        /// Drains the pending queue into the visible TextBox at most ~10 times per
        /// second instead of rebuilding the whole buffer on every AppendLog call.
        /// </summary>
        private void FlushLog()
        {
            if (Dispatcher != null && !Dispatcher.CheckAccess()) return;

            if (_pendingLogLines.Count == 0)
            {
                _logFlushTimer?.Stop();
                return;
            }

            var sb = new StringBuilder(_pendingLogLines.Count * 64);
            while (_pendingLogLines.Count > 0)
            {
                string line = _pendingLogLines.Dequeue();
                _logLines.Enqueue(line);
                if (!_logWarningsOnly || IsDiagnosticLine(line))
                {
                    sb.AppendLine(line);
                }
            }

            bool trimmed = false;
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
                trimmed = true;
            }

            if (trimmed)
            {
                // Only rebuild when the line cap actually forced old lines out,
                // and rebuild through the active filter so nothing leaks back in.
                string visible = BuildVisibleLog();
                if (visible.Length == 0)
                {
                    _logHasContent = false;
                    TxtLog.Text = _logWarningsOnly
                        ? "No warnings or errors so far."
                        : "Ready. Click 'Sync Timeline' to analyze and align multi-track audio.";
                }
                else
                {
                    TxtLog.Text = visible;
                    _logHasContent = true;
                }
            }
            else if (sb.Length > 0)
            {
                if (!_logHasContent)
                {
                    // Drop the "Ready..." placeholder on the first real entry.
                    TxtLog.Text = sb.ToString();
                    _logHasContent = true;
                }
                else
                {
                    TxtLog.AppendText(sb.ToString());
                }
            }
            // Otherwise this flush had nothing visible under the filter: leave
            // whatever is already on screen untouched.

            TxtLog.CaretIndex = TxtLog.Text.Length;
            TxtLog.ScrollToEnd();
        }

        /// <summary>
        /// Forwards the wheel to the page when the log is already at an edge, so the
        /// nested scroll areas do not create a dead zone.
        /// </summary>
        private void TxtLog_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (PageScroller == null) return;

            bool atTop = TxtLog.VerticalOffset <= 0.5;
            bool atBottom = TxtLog.VerticalOffset >= TxtLog.ExtentHeight - TxtLog.ViewportHeight - 0.5;
            if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
            {
                PageScroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = PageScroller
                });
                e.Handled = true;
            }
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FlushLog();
                string text = BuildVisibleLog();
                if (string.IsNullOrEmpty(text))
                {
                    SetStatus("Log is empty.", StatusReadyColor);
                    return;
                }

                Clipboard.SetText(text);
                SetStatus("Log copied to clipboard.", StatusReadyColor);
            }
            catch (Exception ex)
            {
                SetStatus("Could not copy the log.", StatusWarningColor);
                AppendLog("WARNING: could not copy the log to the clipboard: " + ex.Message);
            }
        }
    }
}
