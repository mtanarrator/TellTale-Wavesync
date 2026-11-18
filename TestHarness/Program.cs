using System;
using System.Collections.Generic;
using VegasAudioSync.Interop;
using VegasAudioSync.Models;

namespace TestHarness
{
    class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--screenshot")
            {
                string outPath = args.Length > 1 ? args[1] : "ui_screenshot.png";
                RenderUiScreenshot(outPath);
                return 0;
            }

            if (args.Length > 0 && args[0] == "--longtest")
            {
                return RunLongTakeTest();
            }

            if (args.Length > 0 && args[0] == "--canceltest")
            {
                return RunCancelTest();
            }

            if (args.Length > 0 && args[0] == "--lanetest")
            {
                return RunLaneTest();
            }

            if (args.Length > 0 && args[0] == "--realtest")
            {
                return RunRealMediaTest(args);
            }
            Console.WriteLine("=================================================");
            Console.WriteLine("  VEGAS Audio Sync - Phase 1 Verification Test   ");
            Console.WriteLine("=================================================");
            Console.WriteLine();

            try
            {
                var engine = new NativeSyncEngine();

                // 1. Test Version Query FFI
                Console.WriteLine("[1/3] Querying Native Sync Core Version via FFI...");
                string version = engine.GetEngineVersion();
                Console.WriteLine($"   => Version: {version}");
                if (!version.Contains("vegas_sync_core"))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("   [FAIL] Version string unexpected.");
                    Console.ResetColor();
                    return 1;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   [PASS] Native engine version verified.");
                Console.ResetColor();
                Console.WriteLine();

                // 2. Prepare Sample Multi-Track Request
                Console.WriteLine("[2/3] Constructing Synthetic Timeline Request...");
                var request = new SyncTaskRequest
                {
                    ProjectSampleRate = 48000,
                    MasterTrackId = "Track_MasterAudio",
                    Tracks = new List<TrackDescriptor>
                    {
                        new TrackDescriptor
                        {
                            TrackId = "Track_MasterAudio",
                            Name = "Sound Devices 833 (Boom + Lavs)",
                            IsAudio = true,
                            Events = new List<EventDescriptor>
                            {
                                new EventDescriptor
                                {
                                    EventId = "Evt_Master_001",
                                    TrackId = "Track_MasterAudio",
                                    FilePath = "mock://Take1_Master.wav",
                                    TimelineStartSeconds = 0.0,
                                    TimelineLengthSeconds = 120.0
                                }
                            }
                        },
                        new TrackDescriptor
                        {
                            TrackId = "Track_CamA",
                            Name = "Sony FX6 (Cam A - Scratch Mic)",
                            IsAudio = true,
                            Events = new List<EventDescriptor>
                            {
                                new EventDescriptor
                                {
                                    EventId = "Evt_CamA_001",
                                    TrackId = "Track_CamA",
                                    FilePath = "mock://CamA_0001.mov",
                                    TimelineStartSeconds = 14.5,
                                    TimelineLengthSeconds = 95.0
                                }
                            }
                        },
                        new TrackDescriptor
                        {
                            TrackId = "Track_CamB",
                            Name = "Sony FX3 (Cam B - Room Mic)",
                            IsAudio = true,
                            Events = new List<EventDescriptor>
                            {
                                new EventDescriptor
                                {
                                    EventId = "Evt_CamB_001",
                                    TrackId = "Track_CamB",
                                    FilePath = "mock://CamB_0001.mp4",
                                    TimelineStartSeconds = 18.2,
                                    TimelineLengthSeconds = 80.0
                                }
                            }
                        }
                    },
                    Options = new SyncOptions
                    {
                        DetectDrift = true,
                        SubSampleAccuracy = true,
                        DiscontinuityThresholdMs = 30.0
                    }
                };
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"   [PASS] Created request with {request.Tracks.Count} tracks and 3 events.");
                Console.ResetColor();
                Console.WriteLine();

                // 3. Test Full Pipeline Execution & Progress Callbacks across FFI
                Console.WriteLine("[3/3] Executing Sync Pipeline Across FFI Boundary...");
                int callbackCount = 0;
                SyncManifest manifest = engine.ExecuteSync(request, (progress, message) =>
                {
                    callbackCount++;
                    int barWidth = 30;
                    int filled = (int)(progress * barWidth);
                    string bar = new string('=', Math.Max(0, Math.Min(barWidth, filled))) +
                                 new string(' ', Math.Max(0, barWidth - filled));
                    Console.WriteLine($"   [{bar}] {(progress * 100):0.0}% | {message}");
                });

                Console.WriteLine();
                if (manifest == null || !manifest.Success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("   [FAIL] Pipeline returned null or failed manifest.");
                    Console.ResetColor();
                    return 2;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   [PASS] Manifest received successfully!");
                Console.WriteLine($"   => Session ID:         {manifest.SessionId}");
                Console.WriteLine($"   => Master Track:       {manifest.MasterTrackId}");
                Console.WriteLine($"   => Processed Events:   {manifest.Events.Count}");
                Console.WriteLine($"   => Processing Time:    {manifest.ProcessingTimeMs} ms");
                Console.WriteLine($"   => Callback Invocations: {callbackCount}");
                Console.WriteLine();
                Console.WriteLine("Event Alignment Summary:");
                foreach (var ev in manifest.Events)
                {
                    Console.WriteLine($"   - [{ev.EventId}] Track: {ev.TrackId} | Resolved Start: {ev.ResolvedStartSeconds:F3}s | Drift: {ev.DriftPpm:F2} PPM | Rate: {ev.PlaybackRate:F6} | Conf: {(ev.Confidence * 100):F1}%");
                }
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("=== PHASE 1 VERIFICATION COMPLETE: ALL CHECKS PASSED ===");
                return 0;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FATAL UNCAUGHT ERROR: {ex}");
                Console.ResetColor();
                return 99;
            }
        }

        private static int RunLaneTest()
        {
            Console.WriteLine("=== Crossfade Lane Partitioner Test ===");
            int failures = 0;

            Action<string, int[], int, int> check = (name, lanes, expectedLanes, expectedFirst) =>
            {
                int maxLane = 0;
                foreach (var l in lanes) maxLane = Math.Max(maxLane, l);
                bool ok = lanes.Length > 0 && maxLane + 1 == expectedLanes && lanes[0] == expectedFirst;
                Console.WriteLine($"{(ok ? "[PASS]" : "[FAIL]")} {name}: lanes={maxLane + 1} (expected {expectedLanes})");
                if (!ok) failures++;
            };

            int count;
            var empty = VegasAudioSync.Services.LanePartitioner.Assign(
                new double[0], new double[0], out count);
            Console.WriteLine($"{(count == 0 ? "[PASS]" : "[FAIL]")} empty input: lanes={count}");
            if (count != 0) failures++;

            var single = VegasAudioSync.Services.LanePartitioner.Assign(
                new[] { 0.0 }, new[] { 10.0 }, out count);
            check("single", single, 1, 0);

            var adjacent = VegasAudioSync.Services.LanePartitioner.Assign(
                new[] { 0.0, 10.0 }, new[] { 10.0, 20.0 }, out count);
            check("adjacent events stay on one lane", adjacent, 1, 0);

            var overlap2 = VegasAudioSync.Services.LanePartitioner.Assign(
                new[] { 0.0, 5.0 }, new[] { 10.0, 15.0 }, out count);
            check("two overlapping -> 2 lanes", overlap2, 2, 0);
            if (overlap2[0] == overlap2[1]) { Console.WriteLine("[FAIL] overlapping events share a lane"); failures++; }

            // A [0,10], B [5,15], C [12,22]: A and C can share lane 0, B needs lane 1.
            var chain = VegasAudioSync.Services.LanePartitioner.Assign(
                new[] { 0.0, 5.0, 12.0 }, new[] { 10.0, 15.0, 22.0 }, out count);
            check("staggered overlap -> 2 lanes", chain, 2, 0);
            if (chain[0] != chain[2]) { Console.WriteLine("[FAIL] A and C should share a lane"); failures++; }

            var triple = VegasAudioSync.Services.LanePartitioner.Assign(
                new[] { 0.0, 1.0, 2.0 }, new[] { 10.0, 11.0, 12.0 }, out count);
            check("three-way overlap -> 3 lanes", triple, 3, 0);

            // Pinned (untouched) clips must never be relocated, and a moved clip should
            // still be able to use a free gap on the original track.
            var pinnedGap = VegasAudioSync.Services.LanePartitioner.Assign(
                new[] { 0.0, 12.0, 20.0 },
                new[] { 10.0, 15.0, 30.0 },
                out count,
                1.0,
                new[] { true, false, true });
            check("moved clip fits between pinned clips -> 1 lane", pinnedGap, 1, 0);
            if (pinnedGap[1] != 0) { Console.WriteLine("[FAIL] moved clip should use the original track gap"); failures++; }

            var pinnedConflict = VegasAudioSync.Services.LanePartitioner.Assign(
                new[] { 0.0, 5.0 },
                new[] { 10.0, 15.0 },
                out count,
                1.0,
                new[] { true, false });
            check("moved clip overlapping a pinned clip -> 2 lanes", pinnedConflict, 2, 0);
            if (pinnedConflict[0] != 0 || pinnedConflict[1] != 1) { Console.WriteLine("[FAIL] pinned clip moved or moved clip stayed"); failures++; }

            var intentionalOverlap = VegasAudioSync.Services.LanePartitioner.Assign(
                new[] { 0.0, 5.0 },
                new[] { 10.0, 15.0 },
                out count,
                1.0,
                new[] { true, true });
            check("two pinned (intentional) overlaps stay put -> 1 lane", intentionalOverlap, 1, 0);

            // Deterministic stress: 400 intervals, verify the no-overlap invariant
            // and that we never use more lanes than the maximum concurrent overlap.
            var rnd = new Random(12345);
            int n = 400;
            var starts = new double[n];
            var ends = new double[n];
            for (int i = 0; i < n; i++)
            {
                starts[i] = rnd.Next(0, 100000) / 10.0;
                ends[i] = starts[i] + rnd.Next(1, 20000) / 10.0;
            }

            var assigned = VegasAudioSync.Services.LanePartitioner.Assign(starts, ends, out count);

            bool invariant = true;
            for (int i = 0; i < n && invariant; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (assigned[i] != assigned[j]) continue;
                    // Same lane must not overlap (1 ms tolerance).
                    bool separated = ends[i] <= starts[j] + 1.0 || ends[j] <= starts[i] + 1.0;
                    if (!separated)
                    {
                        Console.WriteLine($"[FAIL] stress: items {i} and {j} overlap in lane {assigned[i]}");
                        invariant = false;
                        break;
                    }
                }
            }

            // Max concurrent overlap via sweep line.
            var points = new List<(double T, int Delta)>();
            for (int i = 0; i < n; i++)
            {
                points.Add((starts[i], +1));
                points.Add((ends[i], -1));
            }
            points.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Delta.CompareTo(b.Delta)); // end first
            int cur = 0, maxConcurrent = 0;
            foreach (var p in points)
            {
                cur += p.Delta;
                if (cur > maxConcurrent) maxConcurrent = cur;
            }

            bool tight = count == maxConcurrent;
            Console.WriteLine($"{(invariant ? "[PASS]" : "[FAIL]")} stress: no overlaps inside a lane (400 intervals)");
            Console.WriteLine($"{(tight ? "[PASS]" : "[FAIL]")} stress: lane count {count} equals maximum concurrent overlap {maxConcurrent}");
            if (!invariant) failures++;
            if (!tight) failures++;

            Console.WriteLine(failures == 0 ? "=== LANE TEST PASSED ===" : "=== LANE TEST FAILED ===");
            return failures == 0 ? 0 : 4;
        }

        private static int RunRealMediaTest(string[] args)
        {
            if (args.Length < 5)
            {
                Console.WriteLine("Usage: --realtest <masterPath> <masterOffsetSec> <masterLenSec> <clipPath>:<durationSec> [...]");
                return 1;
            }

            string masterPath = args[1];
            double masterOffset = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            double masterLen = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);

            string ffmpeg = FindFfmpeg();
            bool sequential = false;
            int firstClipArg = 4;
            if (args.Length > 4 && args[4] == "--sequential")
            {
                sequential = true;
                firstClipArg = 5;
            }

            Console.WriteLine($"Master: {System.IO.Path.GetFileName(masterPath)} offset={masterOffset}s len={masterLen}s");
            Console.WriteLine($"FFmpeg: {ffmpeg}");
            Console.WriteLine();

            var request = new SyncTaskRequest
            {
                ProjectSampleRate = 48000,
                MasterTrackId = "Track_Master",
                Tracks = new List<TrackDescriptor>
                {
                    new TrackDescriptor
                    {
                        TrackId = "Track_Master",
                        Name = "Master",
                        IsAudio = true,
                        Events = new List<EventDescriptor>
                        {
                            new EventDescriptor
                            {
                                EventId = "Event_0_0",
                                TrackId = "Track_Master",
                                FilePath = masterPath,
                                TimelineStartSeconds = 0.0,
                                TimelineLengthSeconds = masterLen,
                                MediaStartSeconds = masterOffset,
                                MediaLengthSeconds = masterLen
                            }
                        }
                    },
                    new TrackDescriptor
                    {
                        TrackId = "Track_Clips",
                        Name = "Clips",
                        IsAudio = true,
                        Events = new List<EventDescriptor>()
                    }
                },
                Options = new SyncOptions
                {
                    DetectDrift = false,
                    DetectDiscontinuities = false,
                    SubSampleAccuracy = true,
                    InternalSampleRate = 16000,
                    FfmpegPath = ffmpeg
                }
            };

            double cursor = 0.0;
            for (int i = firstClipArg; i < args.Length; i++)
            {
                string spec = args[i];
                string path;
                double dur;
                double? recorded = null;

                if (spec.Contains("|"))
                {
                    string[] parts = spec.Split('|');
                    path = parts[0];
                    dur = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2]))
                    {
                        recorded = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    int sep = spec.LastIndexOf(':');
                    path = spec.Substring(0, sep);
                    dur = double.Parse(spec.Substring(sep + 1), System.Globalization.CultureInfo.InvariantCulture);
                }

                string sourceKey = null;
                try { sourceKey = System.IO.Path.GetDirectoryName(path); } catch { }

                if (Environment.GetEnvironmentVariable("WAVESYNC_DEBUG_JSON") == "1")
                {
                    Console.WriteLine($"   clip: {System.IO.Path.GetFileName(path)} dur={dur} recorded={recorded?.ToString() ?? "none"}");
                }

                request.Tracks[1].Events.Add(new EventDescriptor
                {
                    EventId = $"Event_1_{i - firstClipArg}",
                    TrackId = "Track_Clips",
                    FilePath = path,
                    TimelineStartSeconds = sequential ? cursor : 0.0,
                    TimelineLengthSeconds = dur,
                    MediaStartSeconds = 0.0,
                    MediaLengthSeconds = dur,
                    SourceKey = sourceKey,
                    RecordedTimeSeconds = recorded
                });

                cursor += dur;
            }

            var engine = new NativeSyncEngine();
            if (Environment.GetEnvironmentVariable("WAVESYNC_DEBUG_JSON") == "1")
            {
                Console.WriteLine(request.ToJson());
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var manifest = engine.ExecuteSync(request, (p, m) =>
            {
                if (p < 0.21f || p > 0.90f) Console.WriteLine($"   [{p * 100:0.0}%] {m}");
            });
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine($"Elapsed {sw.ElapsedMilliseconds} ms (native {manifest.ProcessingTimeMs} ms)");
            foreach (var w in manifest.Warnings) Console.WriteLine("   NOTE: " + w);
            Console.WriteLine();

            foreach (var ev in manifest.Events)
            {
                if (ev.EventId == "Event_0_0") continue;
                var input = request.Tracks[1].Events.Find(x => x.EventId == ev.EventId);
                Console.WriteLine(
                    $"{System.IO.Path.GetFileName(input.FilePath),-45} dur={input.TimelineLengthSeconds,6:F2}s " +
                    $"resolved={ev.ResolvedStartSeconds,9:F3}s conf={ev.Confidence * 100,6:F1}% aligned={ev.IsAligned} drift={ev.DriftPpm:F1}ppm");
                if (!string.IsNullOrEmpty(ev.Notes)) Console.WriteLine($"    {ev.Notes}");
            }
            return 0;
        }

        private static string FindFfmpeg()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string wingetDir = System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (System.IO.Directory.Exists(wingetDir))
            {
                var matches = System.IO.Directory.GetFiles(wingetDir, "ffmpeg.exe", System.IO.SearchOption.AllDirectories);
                if (matches.Length > 0) return matches[0];
            }
            return "ffmpeg";
        }

        private static int RunLongTakeTest()
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("  TellTale WaveSync - Long-Take (Wedding) Test   ");
            Console.WriteLine("=================================================");
            Console.WriteLine();

            const int rate = 16000;
            const double masterSeconds = 420.0;
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavesync_longtest");
            System.IO.Directory.CreateDirectory(tempDir);

            try
            {
                Console.Write("Generating 7 minute synthetic ceremony recording... ");
                var longSamples = BuildLongRecording(rate, masterSeconds);
                string masterPath = System.IO.Path.Combine(tempDir, "master_long.wav");
                WriteWav(masterPath, rate, longSamples);
                Console.WriteLine("done.");

                var clipStarts = new[] { 31.5, 205.25, 389.0 };
                var clipLengths = new[] { 24.0, 30.0, 22.0 };

                var request = new SyncTaskRequest
                {
                    ProjectSampleRate = 48000,
                    MasterTrackId = "Track_Boom",
                    Tracks = new List<TrackDescriptor>
                    {
                        new TrackDescriptor
                        {
                            TrackId = "Track_Boom",
                            Name = "Ceremony Recorder",
                            IsAudio = true,
                            Events = new List<EventDescriptor>
                            {
                                new EventDescriptor
                                {
                                    EventId = "Event_0_0",
                                    TrackId = "Track_Boom",
                                    FilePath = masterPath,
                                    TimelineStartSeconds = 0.0,
                                    TimelineLengthSeconds = masterSeconds,
                                    MediaStartSeconds = 0.0,
                                    MediaLengthSeconds = masterSeconds
                                }
                            }
                        },
                        new TrackDescriptor
                        {
                            TrackId = "Track_Cams",
                            Name = "Camera Clips",
                            IsAudio = true,
                            Events = new List<EventDescriptor>()
                        }
                    },
                    Options = new SyncOptions
                    {
                        DetectDrift = true,
                        SubSampleAccuracy = true,
                        DetectDiscontinuities = false
                    }
                };

                for (int i = 0; i < clipStarts.Length; i++)
                {
                    string clipPath = System.IO.Path.Combine(tempDir, $"clip_{i}.wav");
                    int startSample = (int)(clipStarts[i] * rate);
                    int lengthSamples = (int)(clipLengths[i] * rate);
                    var clip = new float[lengthSamples];
                    Array.Copy(longSamples, startSample, clip, 0, lengthSamples);
                    WriteWav(clipPath, rate, clip);

                    request.Tracks[1].Events.Add(new EventDescriptor
                    {
                        EventId = $"Event_1_{i}",
                        TrackId = "Track_Cams",
                        FilePath = clipPath,
                        TimelineStartSeconds = 600.0 + i * 90.0,
                        TimelineLengthSeconds = clipLengths[i],
                        MediaStartSeconds = 0.0,
                        MediaLengthSeconds = clipLengths[i]
                    });
                }

                Console.WriteLine("Executing sync across the FFI boundary...");
                var engine = new NativeSyncEngine();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var manifest = engine.ExecuteSync(request, (p, m) => Console.WriteLine($"   [{p * 100:0.0}%] {m}"));
                sw.Stop();

                if (manifest == null || !manifest.Success)
                {
                    Console.WriteLine("[FAIL] No successful manifest.");
                    return 2;
                }

                Console.WriteLine($"Finished in {sw.ElapsedMilliseconds} ms (native reported {manifest.ProcessingTimeMs} ms).");
                foreach (var w in manifest.Warnings) Console.WriteLine("   NOTE: " + w);

                bool allPassed = true;
                for (int i = 0; i < clipStarts.Length; i++)
                {
                    string id = $"Event_1_{i}";
                    var res = manifest.Events.Find(e => e.EventId == id);
                    if (res == null)
                    {
                        Console.WriteLine($"[FAIL] missing result for {id}");
                        allPassed = false;
                        continue;
                    }

                    double error = Math.Abs(res.ResolvedStartSeconds - clipStarts[i]);
                    bool ok = res.IsAligned && error < 0.02;
                    allPassed &= ok;
                    Console.WriteLine(
                        $"{(ok ? "[PASS]" : "[FAIL]")} {id}: resolved {res.ResolvedStartSeconds:F3}s, expected {clipStarts[i]:F3}s, " +
                        $"error {error * 1000:F1} ms, conf {res.Confidence * 100:F1}%");
                }

                Console.WriteLine(allPassed ? "=== LONG-TAKE TEST PASSED ===" : "=== LONG-TAKE TEST FAILED ===");
                return allPassed ? 0 : 3;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 99;
            }
            finally
            {
                try { System.IO.Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static int RunCancelTest()
        {
            Console.WriteLine("=== Cancellation Test ===");
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavesync_canceltest");
            System.IO.Directory.CreateDirectory(tempDir);

            try
            {
                string masterPath = System.IO.Path.Combine(tempDir, "master.wav");
                WriteWav(masterPath, 16000, BuildLongRecording(16000, 240.0));

                var request = new SyncTaskRequest
                {
                    ProjectSampleRate = 48000,
                    Tracks = new List<TrackDescriptor>
                    {
                        new TrackDescriptor
                        {
                            TrackId = "Track_0",
                            Name = "Long take",
                            IsAudio = true,
                            Events = new List<EventDescriptor>
                            {
                                new EventDescriptor
                                {
                                    EventId = "Event_0_0",
                                    TrackId = "Track_0",
                                    FilePath = masterPath,
                                    TimelineStartSeconds = 0,
                                    TimelineLengthSeconds = 240.0,
                                    MediaLengthSeconds = 240.0
                                }
                            }
                        }
                    }
                };

                int callbacks = 0;
                var engine = new NativeSyncEngine();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    engine.ExecuteSync(
                        request,
                        (p, m) => { callbacks++; },
                        () => callbacks >= 2);
                    Console.WriteLine("[FAIL] expected OperationCanceledException");
                    return 2;
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    Console.WriteLine($"[PASS] cancelled after {callbacks} callback(s) in {sw.ElapsedMilliseconds} ms");
                    return sw.ElapsedMilliseconds < 15000 ? 0 : 3;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 99;
            }
            finally
            {
                try { System.IO.Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static float[] BuildLongRecording(int rate, double seconds)
        {
            int n = (int)(seconds * rate);
            var samples = new float[n];
            ulong state = 0xDEADBEEFCAFEBABEUL;

            Func<float> noise = () =>
            {
                state = state * 6364136223846793005UL + 1442695040888963407UL;
                return ((state >> 33) / 2147483648.0f) - 0.5f;
            };

            // Pre-compute a deterministic speech-like burst schedule.
            var schedule = new List<(double Start, double Duration, float Gain)>();
            double t = 0.35;
            uint k = 0;
            while (t < seconds)
            {
                double dur = 0.25 + ((k * 37) % 23) * 0.09;
                float gain = 0.25f + ((k * 53) % 31) / 40.0f;
                if (t + dur < seconds) schedule.Add((t, dur, gain));
                t += 2.5 + ((k * 29) % 17) * 0.21;
                k++;
            }

            for (int i = 0; i < n; i++)
            {
                double time = (double)i / rate;
                float v = noise() * 0.002f;
                foreach (var b in schedule)
                {
                    if (time >= b.Start && time < b.Start + b.Duration)
                    {
                        double local = (time - b.Start) / b.Duration;
                        v += noise() * b.Gain * (float)(0.4 + 0.6 * Math.Abs(Math.Sin(local * 3.1)));
                    }
                }
                samples[i] = Math.Max(-0.99f, Math.Min(0.99f, v));
            }

            return samples;
        }

        private static void WriteWav(string path, int rate, float[] samples)
        {
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            using (var bw = new System.IO.BinaryWriter(fs))
            {
                int dataLen = samples.Length * 2;
                bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataLen);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)1);
                bw.Write(rate);
                bw.Write(rate * 2);
                bw.Write((short)2);
                bw.Write((short)16);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                bw.Write(dataLen);
                foreach (var s in samples)
                {
                    bw.Write((short)(Math.Max(-1f, Math.Min(1f, s)) * 32000f));
                }
            }
        }

        private static void RenderUiScreenshot(string outPath)
        {
            var control = new VegasAudioSync.UI.VegasAudioSyncControl();
            int width = 500;
            int height = 700;
            control.Width = width;
            control.Height = height;

            control.Measure(new System.Windows.Size(width, height));
            control.Arrange(new System.Windows.Rect(0, 0, width, height));
            control.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(control);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));

            using (var fs = System.IO.File.Create(outPath))
            {
                encoder.Save(fs);
            }
            Console.WriteLine($"Screenshot successfully rendered to: {outPath}");
        }
    }
}
