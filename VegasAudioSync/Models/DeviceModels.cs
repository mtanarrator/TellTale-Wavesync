using System;
using System.Collections.Generic;

namespace VegasAudioSync.Models
{
    public enum DeviceKind
    {
        Camera,
        Recorder
    }

    public enum DeviceConfidence
    {
        High,
        Medium,
        Low
    }

    /// <summary>One timeline event that belongs to a device group.</summary>
    public sealed class DeviceEventRef
    {
        public long VegasEventId;
        public int TrackIndex;
        public bool IsVideoEvent;
        public bool IsGrouped;
        public string MediaPath;
        public string MediaFolder;
        public double StartSeconds;
        public double LengthSeconds;
        public double MediaOffsetSeconds;
        public double PlaybackRate = 1.0;
    }

    /// <summary>A detected camera or recorder, with the events it owns.</summary>
    public sealed class DeviceGroup
    {
        public string Id;
        public string Name;
        public DeviceKind Kind;
        public DeviceConfidence Confidence;
        public string Evidence;
        public string Folder;
        public bool Include = true;
        public List<DeviceEventRef> Events = new List<DeviceEventRef>();

        public int ClipCount
        {
            get { return Events != null ? Events.Count : 0; }
        }

        public string KindLabel
        {
            get { return Kind == DeviceKind.Camera ? "Camera" : "Recorder"; }
        }

        public string ConfidenceLabel
        {
            get
            {
                switch (Confidence)
                {
                    case DeviceConfidence.High: return "High";
                    case DeviceConfidence.Medium: return "Medium";
                    default: return "Low";
                }
            }
        }
    }

    public sealed class DeviceScanResult
    {
        public List<DeviceGroup> Groups = new List<DeviceGroup>();
        public List<string> Warnings = new List<string>();
    }

    public sealed class DeviceApplyResult
    {
        public int GroupsApplied;
        public int GroupsSkipped;
        public int TracksCreated;
        public int EventsMoved;
        public List<string> Warnings = new List<string>();
    }
}
