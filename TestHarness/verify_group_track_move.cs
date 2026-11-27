// TellTale WaveSync — VEGAS host probe: reassigning Track on a grouped member
//
// PURPOSE (read-only with respect to your saved project):
//   The applier can relocate whole groups to paired "Sync Lane" tracks, but that
//   path (EnableGroupLaneMove) is disabled until we know how VEGAS reacts when ONE
//   member of a TrackEventGroup is moved to another track.
//
//   This probe answers:
//     1. Does `event.Track = newTrack` move only that member, or the whole group?
//     2. Does the group survive the reassignment (IsGrouped / Count)?
//     3. Can the companion then be moved to the same target track?
//
// HOW TO RUN:
//   1. Save or discard your current project (the script refuses if it is modified).
//   2. VEGAS Pro 2027 -> Tools -> Scripting -> Run Script... -> select this file.
//   3. Read the result dialog. The script creates an UNSAVED temporary project.
//   4. Close the temporary project WITHOUT saving.
//
// The report is also written to:
//   %TEMP%\TellTale_group_track_move.txt
//
// This file is intentionally standalone; it does not reference TellTale assemblies.

using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ScriptPortal.Vegas;

public class EntryPoint
{
    public void FromVegas(Vegas vegas)
    {
        var log = new StringBuilder();
        string wavPath = Path.Combine(Path.GetTempPath(), "telltale_track_move_probe.wav");
        string reportPath = Path.Combine(Path.GetTempPath(), "TellTale_group_track_move.txt");

        try
        {
            if (vegas.Project.IsModified)
            {
                MessageBox.Show(
                    "Please save or discard the current project before running this probe.\n\n" +
                    "The script creates an unsaved temporary project and does not touch your saved file.",
                    "TellTale WaveSync — Group Track Move",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            WriteToneWav(wavPath, 1.0);

            if (!vegas.NewProject())
            {
                MessageBox.Show(
                    "VEGAS did not create a new project; probe aborted.",
                    "TellTale WaveSync — Group Track Move",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            Media media = Media.CreateInstance(vegas.Project, wavPath);
            if (media == null)
                throw new InvalidOperationException("Media.CreateInstance returned null for the probe WAV.");

            AudioTrack sourceA = vegas.Project.AddAudioTrack();
            AudioTrack sourceB = vegas.Project.AddAudioTrack();
            AudioTrack target = vegas.Project.AddAudioTrack();
            sourceA.Name = "Probe Source A";
            sourceB.Name = "Probe Source B";
            target.Name = "Probe Target Lane";

            AudioEvent a = AddEvent(sourceA, media, 0.0);
            AudioEvent b = AddEvent(sourceB, media, 2.0);
            TrackEventGroup group = MakeGroup(vegas, a, b);

            log.AppendLine("=== Initial state ===");
            log.AppendLine("A track: " + TrackIndex(a) + ", B track: " + TrackIndex(b) + ", group count: " + GroupCount(a));
            log.AppendLine();

            a.Track = target;

            log.AppendLine("=== After A.Track = Target ===");
            log.AppendLine("A track: " + TrackIndex(a) + " (expected " + target.Index + ")");
            log.AppendLine("B track: " + TrackIndex(b) + " (unchanged " + sourceB.Index + " means only A moved)");
            log.AppendLine("A.IsGrouped: " + a.IsGrouped + ", B.IsGrouped: " + b.IsGrouped);
            log.AppendLine("Group count: " + GroupCount(a));
            log.AppendLine("B start: " + Seconds(b.Start).ToString("F3") + " s");
            log.AppendLine();

            bool onlyAMoved = TrackIndex(b) == sourceB.Index;
            bool groupSurvived = a.IsGrouped && b.IsGrouped && GroupCount(a) >= 2;

            b.Track = target;

            log.AppendLine("=== After B.Track = Target ===");
            log.AppendLine("A track: " + TrackIndex(a) + ", B track: " + TrackIndex(b));
            log.AppendLine("Group count: " + GroupCount(a));
            log.AppendLine("A.IsGrouped: " + a.IsGrouped + ", B.IsGrouped: " + b.IsGrouped);
            log.AppendLine();

            log.AppendLine("=== INTERPRETATION ===");
            log.AppendLine(onlyAMoved
                ? "Moving one member moves ONLY that member. The group lane path can relocate video and audio members independently."
                : "Moving one member dragged the companion. The group lane path must move the primary only, or compute the resulting placement before moving.");
            log.AppendLine(groupSurvived
                ? "The TrackEventGroup survives a cross-track reassignment; internal offsets can be preserved."
                : "The group did not survive the reassignment. Enabling EnableGroupLaneMove requires re-grouping after the move.");
            log.AppendLine();
            log.AppendLine("Manual checks:");
            log.AppendLine("  - Press Ctrl+Z once: does the whole group return to the original tracks?");
            log.AppendLine("  - Do A and B still move together when you drag A on the timeline?");
            log.AppendLine();
            log.AppendLine("Do NOT save this temporary project. Close it without saving.");

            File.WriteAllText(reportPath, log.ToString());
            MessageBox.Show(
                log.ToString() + Environment.NewLine + "Report saved to:" + Environment.NewLine + reportPath,
                "TellTale WaveSync — Group Track Move",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            string error = "Group track-move probe failed:" + Environment.NewLine + ex;
            try { File.WriteAllText(reportPath, error); } catch { }
            MessageBox.Show(error, "TellTale WaveSync — Group Track Move", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
        }
    }

    private static int TrackIndex(TrackEvent ev)
    {
        try { return ev.Track != null ? ev.Track.Index : -1; }
        catch { return -1; }
    }

    private static int GroupCount(TrackEvent ev)
    {
        try { return ev.Group != null ? ev.Group.Count : -1; }
        catch { return -1; }
    }

    private static AudioEvent AddEvent(AudioTrack track, Media media, double startSeconds)
    {
        AudioEvent ev = track.AddAudioEvent(
            Timecode.FromSeconds(startSeconds),
            Timecode.FromSeconds(1.0));

        AudioStream stream = media.GetAudioStreamByIndex(0);
        if (stream == null)
            throw new InvalidOperationException("The probe WAV has no audio stream.");

        Take take = ev.AddTake(stream);
        if (take == null)
            throw new InvalidOperationException("AddTake returned null for the probe WAV.");

        ev.ActiveTake = take;

        if (ev.ActiveTake == null || ev.ActiveTake.Media == null)
            throw new InvalidOperationException("The audio event has no valid active take/media after AddTake.");

        return ev;
    }

    private static TrackEventGroup MakeGroup(Vegas vegas, TrackEvent first, TrackEvent second)
    {
        if (first == null || second == null)
            throw new InvalidOperationException("Group requires two events.");

        var group = new TrackEventGroup(vegas.Project);

        // Register before adding members (VEGAS rejects AddTrackEvent on an
        // unregistered group).
        vegas.Project.TrackEventGroups.Add(group);
        group.Add(first);
        group.Add(second);

        if (group.Count != 2)
            throw new InvalidOperationException("TrackEventGroup did not accept both events (count=" + group.Count + ").");

        return group;
    }

    private static double Seconds(Timecode tc)
    {
        return tc.ToMilliseconds() / 1000.0;
    }

    private static void WriteToneWav(string path, double seconds)
    {
        const int sampleRate = 48000;
        const short channels = 1;
        const short bits = 16;
        int sampleCount = (int)(sampleRate * seconds);
        int dataBytes = sampleCount * channels * (bits / 8);

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataBytes);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bits / 8));
            writer.Write((short)(channels * (bits / 8)));
            writer.Write(bits);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(dataBytes);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / sampleRate;
                short sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * t) * 12000);
                writer.Write(sample);
            }
        }
    }
}
