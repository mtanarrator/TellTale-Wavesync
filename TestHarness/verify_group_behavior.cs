// TellTale AudioSync — VEGAS host group-behavior verification
//
// PURPOSE (read-only with respect to your saved project):
//   Determines whether VEGAS automatically moves/splits/rate-changes the OTHER
//   members of a TrackEventGroup when AdjustStartLength, Split, or PlaybackRate
//   is called on ONE member.
//
// HOW TO RUN:
//   1. Save or discard your current project (the script refuses if it is modified).
//   2. VEGAS Pro 2027 -> Tools -> Scripting -> Run Script... -> select this file.
//   3. Read the result dialog. The script creates an UNSAVED temporary project.
//   4. Close the temporary project WITHOUT saving.
//
// The report is also written to:
//   %TEMP%\TellTale_group_verification.txt
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
        string wavPath = Path.Combine(Path.GetTempPath(), "TellTale_GroupVerify_Tone.wav");
        string reportPath = Path.Combine(Path.GetTempPath(), "TellTale_group_verification.txt");

        try
        {
            if (vegas.Project.IsModified)
            {
                MessageBox.Show(
                    "Please save or discard the current project before running this verification.\n\n" +
                    "The script creates an unsaved temporary project and does not touch your saved file.",
                    "TellTale AudioSync — Group Verification",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            WriteToneWav(wavPath, 2.0);

            if (!vegas.NewProject())
            {
                MessageBox.Show(
                    "VEGAS did not create a new project; verification aborted.",
                    "TellTale AudioSync — Group Verification",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            Media media = new Media(wavPath);
            AudioStream stream = media.GetAudioStreamByIndex(0);
            AudioTrack track = vegas.Project.AddAudioTrack();

            // --- Test 1: AdjustStartLength on one grouped member -------------------
            AudioEvent a = AddEvent(track, stream, 0.0);
            AudioEvent b = AddEvent(track, stream, 2.0);
            MakeGroup(vegas, a, b);

            double bStartBefore = Seconds(b.Start);
            a.AdjustStartLength(a.Start + Timecode.FromSeconds(5.0), a.Length, false);
            double bStartAfter = Seconds(b.Start);
            double companionShift = bStartAfter - bStartBefore;
            bool autoMove = Math.Abs(companionShift - 5.0) < 0.01;

            log.AppendLine("=== AdjustStartLength(primary + 5.0 s) ===");
            log.AppendLine(string.Format("Companion start before: {0:F3}s", bStartBefore));
            log.AppendLine(string.Format("Companion start after:  {0:F3}s", bStartAfter));
            log.AppendLine(string.Format("Companion moved:        {0:+0.000;-0.000;0.000}s", companionShift));
            log.AppendLine(string.Format("Host auto-move:         {0}", autoMove ? "YES" : "NO"));
            log.AppendLine();

            // --- Test 2: Split on one grouped member -------------------------------
            AudioEvent c = AddEvent(track, stream, 12.0);
            AudioEvent d = AddEvent(track, stream, 14.0);
            MakeGroup(vegas, c, d);

            int eventCountBefore = track.Events.Count;
            double dLengthBefore = Seconds(d.Length);
            c.Split(Timecode.FromSeconds(0.5));
            int eventCountAfter = track.Events.Count;
            double dLengthAfter = Seconds(d.Length);
            bool autoSplit = (eventCountAfter - eventCountBefore) >= 2;

            log.AppendLine("=== Split(primary at +0.5 s) ===");
            log.AppendLine(string.Format("Track event count:      {0} -> {1}", eventCountBefore, eventCountAfter));
            log.AppendLine(string.Format("Companion length:       {0:F3}s -> {1:F3}s", dLengthBefore, dLengthAfter));
            log.AppendLine(string.Format("Host auto-split:        {0}", autoSplit ? "YES" : "NO"));
            log.AppendLine();

            // --- Test 3: PlaybackRate on one grouped member ------------------------
            AudioEvent e = AddEvent(track, stream, 16.0);
            AudioEvent f = AddEvent(track, stream, 18.0);
            MakeGroup(vegas, e, f);

            e.PlaybackRate = 1.05;
            double companionRate = f.PlaybackRate;
            bool autoRate = Math.Abs(companionRate - 1.05) < 1e-6;

            log.AppendLine("=== PlaybackRate(primary = 1.05) ===");
            log.AppendLine(string.Format("Companion playback rate: {0:F6}", companionRate));
            log.AppendLine(string.Format("Host auto-rate:          {0}", autoRate ? "YES" : "NO"));
            log.AppendLine();

            log.AppendLine("=== INTERPRETATION ===");
            log.AppendLine(autoMove
                ? "VEGAS moves grouped companions automatically. The applier MUST NOT apply a manual companion delta (set AutoMoveCompanionsInApplier = false)."
                : "VEGAS leaves grouped companions stationary. The applier MUST apply the same delta to every member (current assumption).");
            log.AppendLine(autoSplit
                ? "VEGAS splits grouped companions automatically. Do not split companions manually."
                : "VEGAS does not split companions automatically. Split every group member at the same global points.");
            log.AppendLine(autoRate
                ? "VEGAS propagates playback rate to grouped companions automatically."
                : "VEGAS does not propagate playback rate automatically; apply it to every group member.");
            log.AppendLine();
            log.AppendLine("Do NOT save this temporary project. Close it without saving.");

            File.WriteAllText(reportPath, log.ToString());
            MessageBox.Show(
                log.ToString() + Environment.NewLine + "Report saved to:" + Environment.NewLine + reportPath,
                "TellTale AudioSync — Group Verification",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            string error = "Group verification failed:" + Environment.NewLine + ex;
            try { File.WriteAllText(reportPath, error); } catch { }
            MessageBox.Show(error, "TellTale AudioSync — Group Verification", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
        }
    }

    private static AudioEvent AddEvent(AudioTrack track, AudioStream stream, double startSeconds)
    {
        AudioEvent ev = track.AddAudioEvent(
            Timecode.FromSeconds(startSeconds),
            Timecode.FromSeconds(1.0));
        ev.AddTake(stream);
        return ev;
    }

    private static TrackEventGroup MakeGroup(Vegas vegas, TrackEvent first, TrackEvent second)
    {
        var group = new TrackEventGroup(vegas.Project);
        group.Add(first);
        group.Add(second);
        vegas.Project.Groups.Add(group);
        return group;
    }

    private static double Seconds(Timecode tc)
    {
        return tc.ToMilliseconds() / 1000.0;
    }

    private static void WriteToneWav(string path, double seconds)
    {
        const int sampleRate = 48000;
        int sampleCount = (int)(sampleRate * seconds);
        int dataLength = sampleCount * 2;

        using (FileStream fs = File.Create(path))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataLength);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataLength);

            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / sampleRate;
                short value = (short)(Math.Sin(2.0 * Math.PI * 440.0 * t) * 10000.0);
                bw.Write(value);
            }
        }
    }
}
