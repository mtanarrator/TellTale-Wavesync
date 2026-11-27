using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VegasAudioSync.Models;
using VegasAudioSync.Services;

namespace VegasAudioSync.UI
{
    /// <summary>
    /// Preview for "Organize Tracks by Device". The dialog edits a private copy of the
    /// scan result; <see cref="Result"/> only contains the committed copy after Apply.
    /// </summary>
    public partial class DeviceOrganizerDialog : Window
    {
        private readonly ObservableCollection<DeviceGroupRow> _rows = new ObservableCollection<DeviceGroupRow>();
        private readonly List<string> _warnings = new List<string>();
        private DeviceScanResult _result;

        /// <summary>Designer-friendly constructor.</summary>
        public DeviceOrganizerDialog()
            : this(null)
        {
        }

        public DeviceOrganizerDialog(DeviceScanResult scan)
        {
            InitializeComponent();

            DeviceScanResult source = scan ?? new DeviceScanResult();
            if (source.Warnings != null)
            {
                _warnings.AddRange(source.Warnings);
            }

            // Work on copies so the caller's scan (and its Groups list) is only read here.
            _result = CloneResult(source);
            foreach (DeviceGroup group in _result.Groups)
            {
                _rows.Add(new DeviceGroupRow(group, OnRowChanged));
            }

            LvGroups.ItemsSource = _rows;
            UpdateSummary();
            UpdateCommandState();

            Loaded += DeviceOrganizerDialog_Loaded;
        }

        /// <summary>Edited groups after Apply. Never the caller's original list.</summary>
        public DeviceScanResult Result
        {
            get { return _result; }
        }

        private void DeviceOrganizerDialog_Loaded(object sender, RoutedEventArgs e)
        {
            if (_rows.Count > 0)
            {
                LvGroups.SelectedIndex = 0;
            }
            LvGroups.Focus();
        }

        private void OnRowChanged()
        {
            UpdateSummary();
        }

        private void LvGroups_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCommandState();
        }

        private void BtnMerge_Click(object sender, RoutedEventArgs e)
        {
            List<DeviceGroupRow> selected = GetSelectedRowsInOrder();
            if (selected.Count < 2)
            {
                return;
            }

            DeviceGroupRow targetRow = selected[0];
            DeviceGroup target = targetRow.Model;
            bool anyCamera = target.Kind == DeviceKind.Camera;

            if (target.Events == null)
            {
                target.Events = new List<DeviceEventRef>();
            }

            for (int i = 1; i < selected.Count; i++)
            {
                DeviceGroupRow row = selected[i];
                DeviceGroup other = row.Model;

                anyCamera = anyCamera || other.Kind == DeviceKind.Camera;
                MergeEvents(target.Events, other.Events);
                _rows.Remove(row);
            }

            // Camera wins; the target's name and confidence stay as they were.
            if (anyCamera)
            {
                target.Kind = DeviceKind.Camera;
            }
            target.Evidence = "merged";
            targetRow.RefreshAll();

            LvGroups.SelectedItems.Clear();
            LvGroups.SelectedItem = targetRow;
            UpdateSummary();
            UpdateCommandState();
        }

        private void BtnSplit_Click(object sender, RoutedEventArgs e)
        {
            List<DeviceGroupRow> selected = GetSelectedRowsInOrder();
            if (selected.Count == 0)
            {
                return;
            }

            foreach (DeviceGroupRow row in selected)
            {
                DeviceGroup source = row.Model;
                List<FolderBucket> buckets = GroupByFolder(source.Events);
                if (buckets.Count < 2)
                {
                    continue; // all clips share one folder, nothing to split
                }

                int insertAt = _rows.IndexOf(row);
                if (insertAt < 0)
                {
                    continue;
                }
                _rows.RemoveAt(insertAt);

                for (int i = 0; i < buckets.Count; i++)
                {
                    FolderBucket bucket = buckets[i];

                    DeviceGroup copy = CloneGroup(source);
                    copy.Events = bucket.Events;
                    copy.Folder = bucket.Folder;
                    if (!string.IsNullOrEmpty(source.Id))
                    {
                        copy.Id = source.Id + "-f" + (i + 1);
                    }

                    string folderLabel = FolderLeaf(bucket.Folder);
                    if (folderLabel.Length == 0)
                    {
                        folderLabel = "(no folder)";
                    }
                    copy.Name = source.Name + " \u2014 " + folderLabel;
                    copy.Evidence = string.IsNullOrWhiteSpace(source.Evidence)
                        ? "folder " + folderLabel
                        : source.Evidence + " · folder " + folderLabel;

                    _rows.Insert(insertAt + i, new DeviceGroupRow(copy, OnRowChanged));
                }
            }

            LvGroups.SelectedItems.Clear();
            UpdateSummary();
            UpdateCommandState();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (DeviceGroupRow row in _rows)
            {
                row.Include = true;
            }
            UpdateSummary();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (DeviceGroupRow row in _rows)
            {
                row.Include = false;
            }
            UpdateSummary();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Finish(false);
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            Finish(true);
        }

        private void Finish(bool apply)
        {
            if (apply)
            {
                _result = BuildResult();
            }

            try
            {
                DialogResult = apply;
            }
            catch (InvalidOperationException)
            {
                // Window was shown with Show() instead of ShowDialog().
                Close();
            }
        }

        private DeviceScanResult BuildResult()
        {
            DeviceScanResult result = new DeviceScanResult();
            result.Warnings.AddRange(_warnings);

            int unnamed = 0;
            foreach (DeviceGroupRow row in _rows)
            {
                DeviceGroup group = row.Model;
                group.Name = (group.Name ?? string.Empty).Trim();
                if (group.Name.Length == 0)
                {
                    unnamed++;
                    group.Name = (group.Kind == DeviceKind.Camera ? "Camera " : "Recorder ") + unnamed;
                }
                if (group.Events == null)
                {
                    group.Events = new List<DeviceEventRef>();
                }
                result.Groups.Add(group);
            }

            return result;
        }

        private void UpdateSummary()
        {
            int cameras = 0;
            int recorders = 0;
            int clips = 0;

            foreach (DeviceGroupRow row in _rows)
            {
                if (!row.Include)
                {
                    continue;
                }

                if (row.Model.Kind == DeviceKind.Camera)
                {
                    cameras++;
                }
                else
                {
                    recorders++;
                }
                clips += row.Model.ClipCount;
            }

            if (cameras + recorders == 0)
            {
                TxtSummary.Text = "No devices included; Apply is disabled.";
            }
            else
            {
                TxtSummary.Text = Plural(cameras, "camera", "cameras") + " · " +
                                  Plural(recorders, "recorder", "recorders") + " · " +
                                  Plural(clips, "clip", "clips") + " included";
            }

            BtnApply.IsEnabled = cameras + recorders > 0;
        }

        private void UpdateCommandState()
        {
            int selected = LvGroups.SelectedItems == null ? 0 : LvGroups.SelectedItems.Count;
            BtnMerge.IsEnabled = selected >= 2;
            BtnSplit.IsEnabled = selected >= 1;
        }

        private List<DeviceGroupRow> GetSelectedRowsInOrder()
        {
            List<DeviceGroupRow> selected = new List<DeviceGroupRow>();
            if (LvGroups.SelectedItems == null)
            {
                return selected;
            }

            // Walk the source collection so the display order decides "first selected".
            foreach (DeviceGroupRow row in _rows)
            {
                if (LvGroups.SelectedItems.Contains(row))
                {
                    selected.Add(row);
                }
            }
            return selected;
        }

        private static string Plural(int count, string singular, string plural)
        {
            return count + " " + (count == 1 ? singular : plural);
        }

        private static void MergeEvents(List<DeviceEventRef> target, List<DeviceEventRef> source)
        {
            if (source == null)
            {
                return;
            }

            HashSet<long> known = new HashSet<long>();
            foreach (DeviceEventRef existing in target)
            {
                if (existing != null && existing.VegasEventId != 0)
                {
                    known.Add(existing.VegasEventId);
                }
            }

            foreach (DeviceEventRef candidate in source)
            {
                if (candidate == null)
                {
                    continue;
                }
                if (candidate.VegasEventId != 0)
                {
                    if (!known.Add(candidate.VegasEventId))
                    {
                        continue;
                    }
                }
                else if (target.Contains(candidate))
                {
                    continue;
                }
                target.Add(candidate);
            }
        }

        private static List<FolderBucket> GroupByFolder(List<DeviceEventRef> events)
        {
            List<FolderBucket> buckets = new List<FolderBucket>();
            Dictionary<string, FolderBucket> map = new Dictionary<string, FolderBucket>(StringComparer.OrdinalIgnoreCase);

            if (events != null)
            {
                foreach (DeviceEventRef ev in events)
                {
                    if (ev == null)
                    {
                        continue;
                    }

                    string folder = ev.MediaFolder ?? string.Empty;
                    string key = folder.Trim().TrimEnd('\\', '/');

                    FolderBucket bucket;
                    if (!map.TryGetValue(key, out bucket))
                    {
                        bucket = new FolderBucket();
                        bucket.Folder = folder;
                        bucket.Key = key;
                        map.Add(key, bucket);
                        buckets.Add(bucket);
                    }
                    bucket.Events.Add(ev);
                }
            }

            buckets.Sort(delegate(FolderBucket a, FolderBucket b)
            {
                bool aEmpty = a.Key.Length == 0;
                bool bEmpty = b.Key.Length == 0;
                if (aEmpty != bEmpty)
                {
                    return aEmpty ? 1 : -1;
                }
                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            return buckets;
        }

        private static string FolderLeaf(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return string.Empty;
            }

            string trimmed = folder.Trim().TrimEnd('\\', '/');
            int slash = trimmed.LastIndexOfAny(new char[] { '\\', '/' });
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        private static DeviceScanResult CloneResult(DeviceScanResult source)
        {
            DeviceScanResult clone = new DeviceScanResult();
            if (source.Warnings != null)
            {
                clone.Warnings.AddRange(source.Warnings);
            }
            if (source.Groups != null)
            {
                foreach (DeviceGroup group in source.Groups)
                {
                    if (group != null)
                    {
                        clone.Groups.Add(CloneGroup(group));
                    }
                }
            }
            return clone;
        }

        private static DeviceGroup CloneGroup(DeviceGroup source)
        {
            DeviceGroup copy = new DeviceGroup();
            copy.Id = source.Id;
            copy.Name = source.Name;
            copy.Kind = source.Kind;
            copy.Confidence = source.Confidence;
            copy.Evidence = source.Evidence;
            copy.Folder = source.Folder;
            copy.Include = source.Include;
            copy.Events = source.Events != null
                ? new List<DeviceEventRef>(source.Events)
                : new List<DeviceEventRef>();
            return copy;
        }

        private sealed class FolderBucket
        {
            public string Folder;
            public string Key;
            public List<DeviceEventRef> Events = new List<DeviceEventRef>();
        }
    }

    /// <summary>
    /// Row view model: DeviceGroup exposes public fields, which WPF cannot bind to.
    /// These properties read and write the underlying group so the dialog can edit
    /// a copy of the scan without touching the caller's model.
    /// </summary>
    internal sealed class DeviceGroupRow : INotifyPropertyChanged
    {
        private readonly Action _changed;

        public DeviceGroupRow(DeviceGroup model, Action changed)
        {
            Model = model ?? new DeviceGroup();
            _changed = changed;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public DeviceGroup Model { get; private set; }

        public bool Include
        {
            get { return Model.Include; }
            set
            {
                if (Model.Include == value)
                {
                    return;
                }
                Model.Include = value;
                Raise("Include");
                if (_changed != null)
                {
                    _changed();
                }
            }
        }

        public string Name
        {
            get { return Model.Name ?? string.Empty; }
            set
            {
                string name = value ?? string.Empty;
                if (string.Equals(Model.Name ?? string.Empty, name, StringComparison.Ordinal))
                {
                    return;
                }
                Model.Name = name;
                Raise("Name");
            }
        }

        public string KindLabel
        {
            get { return Model.KindLabel; }
        }

        /// <summary>
        /// Preview-only role. B-roll is a camera group whose clips are all shorter
        /// than the b-roll threshold; it never changes the track assignment.
        /// </summary>
        public string RoleLabel
        {
            get { return DeviceOrganizer.RoleLabel(Model, DeviceOrganizer.DefaultBrollMaxSeconds); }
        }

        public string ConfidenceLabel
        {
            get { return Model.ConfidenceLabel; }
        }

        public int ClipCount
        {
            get { return Model.ClipCount; }
        }

        public string Evidence
        {
            get { return Model.Evidence ?? string.Empty; }
        }

        public void RefreshAll()
        {
            Raise("Include");
            Raise("Name");
            Raise("KindLabel");
            Raise("RoleLabel");
            Raise("ConfidenceLabel");
            Raise("ClipCount");
            Raise("Evidence");
        }

        private void Raise(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
