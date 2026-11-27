use std::fs::File;
use std::io::{BufReader, Read};
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};

use symphonia::core::audio::{AudioBufferRef, Signal};
use symphonia::core::codecs::{DecoderOptions, CODEC_TYPE_NULL};
use symphonia::core::errors::Error as SymphoniaError;
use symphonia::core::formats::FormatOptions;
use symphonia::core::io::MediaSourceStream;
use symphonia::core::meta::MetadataOptions;
use symphonia::core::probe::Hint;

use super::dsp::{DspProcessor, StreamingBandpassFilter};
use crate::correlation::EnergyEnvelopeBuilder;

/// Sentinel error used to propagate user cancellation through the pipeline.
pub const CANCELLED: &str = "__wavesync_cancelled__";

const STDERR_LIMIT: usize = 16 * 1024;
const READ_CHUNK_BYTES: usize = 1 << 20;

/// Set once a spawn fails with `NotFound`, so a missing FFmpeg does not cause one
/// failed process launch per event (which can happen hundreds of times on a big
/// wedding timeline).
static FFMPEG_UNAVAILABLE: AtomicBool = AtomicBool::new(false);

fn ffmpeg_marked_unavailable() -> bool {
    FFMPEG_UNAVAILABLE.load(Ordering::Relaxed)
}

/// Resets the FFmpeg availability probe before a new synchronization run.
pub fn reset_ffmpeg_availability() {
    FFMPEG_UNAVAILABLE.store(false, Ordering::Relaxed);
}

/// Progress sink: receives a fraction in [0, 1] and returns `false` to cancel.
pub type DecodeProgress<'a> = Option<&'a (dyn Fn(f32) -> bool + Sync)>;

pub struct AudioDecoder;

impl AudioDecoder {
    pub fn is_video_container(file_path: &str) -> bool {
        Path::new(file_path)
            .extension()
            .and_then(|e| e.to_str())
            .map(|ext| {
                let e = ext.to_lowercase();
                matches!(
                    e.as_str(),
                    "mp4" | "mov" | "mkv" | "mxf" | "mts" | "m2ts" | "avi" | "m4v" | "wmv" | "webm"
                )
            })
            .unwrap_or(false)
    }

    /// Loads, decodes, resamples, and conditions any media file (audio or video container)
    /// into a normalized 16 kHz mono f32 buffer.
    pub fn load_and_condition(
        file_path: &str,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
    ) -> Result<Vec<f32>, String> {
        Self::load_and_condition_with_progress_for_stream(
            file_path,
            0,
            start_offset_sec,
            duration_sec,
            target_rate,
            ffmpeg_bin,
            None,
        )
    }

    /// Same as [`load_and_condition`], targeting a specific 0-based audio stream index.
    pub fn load_and_condition_for_stream(
        file_path: &str,
        stream_index: u32,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
    ) -> Result<Vec<f32>, String> {
        Self::load_and_condition_with_progress_for_stream(
            file_path,
            stream_index,
            start_offset_sec,
            duration_sec,
            target_rate,
            ffmpeg_bin,
            None,
        )
    }

    /// Same as [`load_and_condition`], with progress reporting and cancellation support.
    pub fn load_and_condition_with_progress(
        file_path: &str,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
        progress: DecodeProgress<'_>,
    ) -> Result<Vec<f32>, String> {
        Self::load_and_condition_with_progress_for_stream(
            file_path,
            0,
            start_offset_sec,
            duration_sec,
            target_rate,
            ffmpeg_bin,
            progress,
        )
    }

    /// Stream-aware variant of [`load_and_condition_with_progress`].
    pub fn load_and_condition_with_progress_for_stream(
        file_path: &str,
        stream_index: u32,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
        progress: DecodeProgress<'_>,
    ) -> Result<Vec<f32>, String> {
        let path = Path::new(file_path);
        if !path.exists() {
            return Err(format!("Media file does not exist on disk: '{}'", file_path));
        }

        let is_video_container = Self::is_video_container(file_path);

        // Video containers always need FFmpeg (Symphonia has no H.264/HEVC decoders),
        // so do not waste time on a doomed fallback when a clip simply has no audio.
        if is_video_container {
            return Self::decode_with_ffmpeg_progress(
                file_path,
                stream_index,
                start_offset_sec,
                duration_sec,
                target_rate,
                ffmpeg_bin,
                progress,
            );
        }

        // Audio files with a requested slice are also decoded through FFmpeg so that
        // only the requested span is materialized (decoding a full 2 hour take just
        // to use 30 seconds would exhaust memory).
        if start_offset_sec.is_some() || duration_sec.is_some() {
            match Self::decode_with_ffmpeg_progress(
                file_path,
                stream_index,
                start_offset_sec,
                duration_sec,
                target_rate,
                ffmpeg_bin,
                progress,
            ) {
                Ok(samples) => return Ok(samples),
                Err(e) if e == CANCELLED => return Err(e),
                Err(ffmpeg_err) => {
                    log::warn!(
                        "FFmpeg stream decode failed for '{}': {}. Falling back to Symphonia.",
                        file_path,
                        ffmpeg_err
                    );
                }
            }
        }

        // Pure-Rust Symphonia path (WAV, FLAC, MP3, ...)
        match Self::decode_with_symphonia_stream(file_path, stream_index) {
            Ok((raw_mono, sample_rate)) => {
                let mut samples = DspProcessor::condition_audio(&raw_mono, sample_rate, target_rate)?;
                if let Some(ss) = start_offset_sec {
                    let start_idx = (ss * target_rate as f64) as usize;
                    if start_idx < samples.len() {
                        samples = samples[start_idx..].to_vec();
                    } else {
                        samples.clear();
                    }
                }
                if let Some(t) = duration_sec {
                    let take_len = (t * target_rate as f64) as usize;
                    if take_len < samples.len() {
                        samples.truncate(take_len);
                    }
                }
                if let Some(cb) = progress {
                    cb(1.0);
                }
                Ok(samples)
            }
            Err(symph_err) => {
                // Last ditch effort with FFmpeg
                Self::decode_with_ffmpeg_progress(
                    file_path,
                    stream_index,
                    start_offset_sec,
                    duration_sec,
                    target_rate,
                    ffmpeg_bin,
                    progress,
                )
                .map_err(|e| {
                    if e == CANCELLED {
                        e
                    } else {
                        format!(
                            "Failed to decode '{}'. Symphonia error: [{}]. FFmpeg error: [{}]",
                            file_path, symph_err, e
                        )
                    }
                })
            }
        }
    }

    /// Pure-Rust decoding using Symphonia. Returns mono f32 samples and the original sample rate.
    pub fn decode_with_symphonia(file_path: &str) -> Result<(Vec<f32>, u32), String> {
        Self::decode_with_symphonia_stream(file_path, 0)
    }

    /// Pure-Rust decoding using Symphonia for a specific 0-based audio stream index.
    pub fn decode_with_symphonia_stream(
        file_path: &str,
        stream_index: u32,
    ) -> Result<(Vec<f32>, u32), String> {
        let file = File::open(file_path).map_err(|e| format!("Cannot open file '{}': {}", file_path, e))?;
        let mss = MediaSourceStream::new(Box::new(file), Default::default());

        let mut hint = Hint::new();
        if let Some(ext) = Path::new(file_path).extension().and_then(|e| e.to_str()) {
            hint.with_extension(ext);
        }

        let format_opts = FormatOptions {
            enable_gapless: true,
            ..Default::default()
        };
        let meta_opts: MetadataOptions = Default::default();

        let probed = symphonia::default::get_probe()
            .format(&hint, mss, &format_opts, &meta_opts)
            .map_err(|e| format!("Format probe failed: {}", e))?;

        let mut format = probed.format;

        let audio_tracks: Vec<_> = format
            .tracks()
            .iter()
            .filter(|t| t.codec_params.codec != CODEC_TYPE_NULL)
            .collect();
        let track = audio_tracks
            .get(stream_index as usize)
            .or_else(|| audio_tracks.first())
            .copied()
            .ok_or_else(|| "No supported audio tracks found in file".to_string())?;

        let track_id = track.id;
        let sample_rate = track
            .codec_params
            .sample_rate
            .ok_or_else(|| "Sample rate is unspecified".to_string())?;

        let dec_opts: DecoderOptions = Default::default();
        let mut decoder = symphonia::default::get_codecs()
            .make(&track.codec_params, &dec_opts)
            .map_err(|e| format!("Unsupported codec: {}", e))?;

        let mut mono_samples = Vec::new();

        loop {
            let packet = match format.next_packet() {
                Ok(packet) => packet,
                Err(SymphoniaError::IoError(ref e))
                    if e.kind() == std::io::ErrorKind::UnexpectedEof =>
                {
                    break;
                }
                Err(SymphoniaError::ResetRequired) => {
                    decoder.reset();
                    continue;
                }
                Err(e) => return Err(format!("Error reading packet: {}", e)),
            };

            if packet.track_id() != track_id {
                continue;
            }

            match decoder.decode(&packet) {
                Ok(decoded) => {
                    Self::append_decoded_to_mono(&decoded, &mut mono_samples);
                }
                Err(SymphoniaError::IoError(ref e))
                    if e.kind() == std::io::ErrorKind::UnexpectedEof =>
                {
                    break;
                }
                Err(SymphoniaError::DecodeError(msg)) => {
                    log::warn!("Decode warning: {}", msg);
                    continue;
                }
                Err(e) => return Err(format!("Decoder error: {}", e)),
            }
        }

        if mono_samples.is_empty() {
            return Err("Decoded stream was empty".into());
        }

        Ok((mono_samples, sample_rate))
    }

    fn append_decoded_to_mono(decoded: &AudioBufferRef, output: &mut Vec<f32>) {
        match decoded {
            AudioBufferRef::F32(buf) => {
                let channels = buf.spec().channels.count();
                let frames = buf.frames();
                output.reserve(frames);
                for i in 0..frames {
                    let mut sum = 0.0f32;
                    for ch in 0..channels {
                        sum += buf.chan(ch)[i];
                    }
                    output.push(sum / channels as f32);
                }
            }
            AudioBufferRef::S16(buf) => {
                let channels = buf.spec().channels.count();
                let frames = buf.frames();
                output.reserve(frames);
                let inv_scale = 1.0 / 32768.0f32;
                for i in 0..frames {
                    let mut sum = 0.0f32;
                    for ch in 0..channels {
                        sum += buf.chan(ch)[i] as f32 * inv_scale;
                    }
                    output.push(sum / channels as f32);
                }
            }
            AudioBufferRef::S32(buf) => {
                let channels = buf.spec().channels.count();
                let frames = buf.frames();
                output.reserve(frames);
                let inv_scale = 1.0 / 2147483648.0f32;
                for i in 0..frames {
                    let mut sum = 0.0f32;
                    for ch in 0..channels {
                        sum += buf.chan(ch)[i] as f32 * inv_scale;
                    }
                    output.push(sum / channels as f32);
                }
            }
            AudioBufferRef::U8(buf) => {
                let channels = buf.spec().channels.count();
                let frames = buf.frames();
                output.reserve(frames);
                for i in 0..frames {
                    let mut sum = 0.0f32;
                    for ch in 0..channels {
                        sum += (buf.chan(ch)[i] as f32 - 128.0) / 128.0;
                    }
                    output.push(sum / channels as f32);
                }
            }
            AudioBufferRef::S24(buf) => {
                let channels = buf.spec().channels.count();
                let frames = buf.frames();
                output.reserve(frames);
                let inv_scale = 1.0 / 8388608.0f32;
                for i in 0..frames {
                    let mut sum = 0.0f32;
                    for ch in 0..channels {
                        sum += buf.chan(ch)[i].0 as f32 * inv_scale;
                    }
                    output.push(sum / channels as f32);
                }
            }
            _ => {
                log::warn!("Unhandled AudioBufferRef format, skipping chunk");
            }
        }
    }

    /// Streaming decode using the FFmpeg CLI pipe. Decodes directly into f32 mono at
    /// `target_rate`. Memory use stays bounded regardless of the media duration.
    pub fn decode_with_ffmpeg(
        file_path: &str,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
    ) -> Result<Vec<f32>, String> {
        Self::decode_with_ffmpeg_progress(
            file_path,
            0,
            start_offset_sec,
            duration_sec,
            target_rate,
            ffmpeg_bin,
            None,
        )
    }

    fn decode_with_ffmpeg_progress(
        file_path: &str,
        stream_index: u32,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
        progress: DecodeProgress<'_>,
    ) -> Result<Vec<f32>, String> {
        let expected = duration_sec.map(|d| (d.max(0.0) * target_rate as f64) as usize);
        let mut samples: Vec<f32> = Vec::with_capacity(expected.unwrap_or(0));
        let mut total: u64 = 0;
        let mut last_reported: f32 = 0.0;

        Self::stream_ffmpeg_audio(
            file_path,
            stream_index,
            start_offset_sec,
            duration_sec,
            target_rate,
            ffmpeg_bin,
            |chunk: &[f32]| {
                samples.extend_from_slice(chunk);
                total += chunk.len() as u64;
                if let (Some(cb), Some(exp)) = (progress, expected) {
                    if exp > 0 {
                        let frac = (total as f32 / exp as f32).min(1.0);
                        if frac - last_reported >= 0.02 || (frac >= 1.0 && last_reported < 1.0) {
                            last_reported = frac;
                            return cb(frac);
                        }
                    }
                }
                true
            },
        )?;

        if samples.len() < 4 {
            return Err("FFmpeg output contained no audio samples".into());
        }

        // Apply FIR bandpass filter & DC removal
        let fir_kernel = DspProcessor::design_bandpass_fir(65, 300.0, 4000.0, target_rate as f32);
        DspProcessor::remove_dc_offset(&mut samples);
        let mut filtered = DspProcessor::apply_fir_filter(&samples, &fir_kernel);
        drop(samples);

        // Normalize
        let max_amp = filtered
            .iter()
            .map(|&s| s.abs())
            .fold(0.0f32, |a, b| a.max(b));
        if max_amp > 1e-6 {
            let inv_max = 1.0 / max_amp;
            for s in filtered.iter_mut() {
                *s *= inv_max;
            }
        }

        Ok(filtered)
    }

    /// Computes a compact log-energy envelope for an arbitrary long media file
    /// without ever materializing the full PCM buffer.
    pub fn scan_energy_envelope(
        file_path: &str,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
        frame_size: usize,
        hop_size: usize,
        progress: DecodeProgress<'_>,
    ) -> Result<Vec<f32>, String> {
        Self::scan_energy_envelope_for_stream(
            file_path,
            0,
            start_offset_sec,
            duration_sec,
            target_rate,
            ffmpeg_bin,
            frame_size,
            hop_size,
            progress,
        )
    }

    /// Stream-aware variant of [`scan_energy_envelope`]. Applies a streaming
    /// 300–4000 Hz bandpass filter so long-take envelopes match the vocal/event
    /// band conditioning used on shorter clips.
    #[allow(clippy::too_many_arguments)]
    pub fn scan_energy_envelope_for_stream(
        file_path: &str,
        stream_index: u32,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
        frame_size: usize,
        hop_size: usize,
        progress: DecodeProgress<'_>,
    ) -> Result<Vec<f32>, String> {
        let path = Path::new(file_path);
        if !path.exists() {
            return Err(format!("Media file does not exist on disk: '{}'", file_path));
        }

        let expected_frames = duration_sec
            .map(|d| ((d.max(0.0) * target_rate as f64) as usize / hop_size.max(1)) + 8)
            .unwrap_or(0);

        let try_ffmpeg = Self::is_video_container(file_path)
            || start_offset_sec.is_some()
            || duration_sec.is_some();

        if try_ffmpeg {
            let mut builder =
                EnergyEnvelopeBuilder::with_capacity(frame_size, hop_size, expected_frames);
            let mut bandpass = StreamingBandpassFilter::new(300.0, 4000.0, target_rate as f64);
            let mut filtered_buf = Vec::new();
            let expected = duration_sec.map(|d| (d.max(0.0) * target_rate as f64) as usize);
            let mut total: u64 = 0;
            let mut last_reported: f32 = 0.0;

            let result = Self::stream_ffmpeg_audio(
                file_path,
                stream_index,
                start_offset_sec,
                duration_sec,
                target_rate,
                ffmpeg_bin,
                |chunk: &[f32]| {
                    filtered_buf.clear();
                    filtered_buf.extend_from_slice(chunk);
                    bandpass.process_in_place(&mut filtered_buf);
                    builder.push(&filtered_buf);
                    total += chunk.len() as u64;
                    if let (Some(cb), Some(exp)) = (progress, expected) {
                        if exp > 0 {
                            let frac = (total as f32 / exp as f32).min(1.0);
                            if frac - last_reported >= 0.02 || (frac >= 1.0 && last_reported < 1.0) {
                                last_reported = frac;
                                return cb(frac);
                            }
                        }
                    }
                    true
                },
            );

            match result {
                Ok(_) if builder.frames() > 0 => {
                    return Ok(builder.finish());
                }
                Ok(_) => return Err("Decoded stream was empty".into()),
                Err(e) if e == CANCELLED => return Err(e),
                Err(e) => {
                    if Self::is_video_container(file_path) {
                        return Err(e);
                    }
                    log::warn!(
                        "FFmpeg envelope scan failed for '{}': {}. Falling back to Symphonia.",
                        file_path,
                        e
                    );
                }
            }
        }

        // Fallback: in-memory decode (only used when FFmpeg is unavailable).
        let (raw_mono, sample_rate) = Self::decode_with_symphonia_stream(file_path, stream_index)?;
        let resampled = if sample_rate != target_rate {
            DspProcessor::resample_mono(&raw_mono, sample_rate, target_rate)?
        } else {
            raw_mono
        };

        let start_idx = start_offset_sec
            .map(|s| (s * target_rate as f64) as usize)
            .unwrap_or(0)
            .min(resampled.len());
        let end_idx = duration_sec
            .map(|d| start_idx + (d * target_rate as f64) as usize)
            .unwrap_or(resampled.len())
            .min(resampled.len());

        let mut builder =
            EnergyEnvelopeBuilder::with_capacity(frame_size, hop_size, expected_frames);
        if start_idx < end_idx {
            let mut slice = resampled[start_idx..end_idx].to_vec();
            drop(resampled);
            let mut bandpass = StreamingBandpassFilter::new(300.0, 4000.0, target_rate as f64);
            bandpass.process_in_place(&mut slice);
            builder.push(&slice);
        } else {
            drop(resampled);
        }

        if let Some(cb) = progress {
            cb(1.0);
        }

        if builder.frames() == 0 {
            return Err("Decoded stream was empty".into());
        }

        Ok(builder.finish())
    }

    /// Spawns FFmpeg and feeds decoded f32 mono chunks to `on_samples`.
    /// Rows are read in fixed-size chunks so memory stays flat for any duration.
    fn stream_ffmpeg_audio<F>(
        file_path: &str,
        stream_index: u32,
        start_offset_sec: Option<f64>,
        duration_sec: Option<f64>,
        target_rate: u32,
        ffmpeg_bin: Option<&str>,
        mut on_samples: F,
    ) -> Result<u64, String>
    where
        F: FnMut(&[f32]) -> bool,
    {
        let bin = ffmpeg_bin.unwrap_or("ffmpeg");

        if ffmpeg_marked_unavailable() {
            return Err(format!(
                "FFmpeg at '{}' is unavailable (already determined earlier in this run)",
                bin
            ));
        }

        let mut cmd = Command::new(bin);
        cmd.arg("-nostdin")
            .arg("-hide_banner")
            .arg("-nostats")
            .arg("-loglevel")
            .arg("error");

        // Hybrid fast + sample-accurate seek: jump to 2s before the target at the
        // demuxer level, then trim the final <=2s after decoding.
        let mut post_seek = 0.0f64;
        if let Some(ss) = start_offset_sec {
            if ss > 2.0 {
                let pre_seek = ss - 2.0;
                post_seek = 2.0;
                cmd.arg("-ss").arg(format!("{:.6}", pre_seek));
            } else if ss > 0.001 {
                post_seek = ss;
            }
        }

        cmd.arg("-i").arg(file_path);

        if post_seek > 0.001 {
            cmd.arg("-ss").arg(format!("{:.6}", post_seek));
        }
        if let Some(t) = duration_sec {
            if t > 0.001 {
                cmd.arg("-t").arg(format!("{:.6}", t));
            }
        }

        cmd.arg("-map")
            .arg(format!("0:a:{}?", stream_index))
            .arg("-acodec")
            .arg("pcm_f32le")
            .arg("-ar")
            .arg(target_rate.to_string())
            .arg("-ac")
            .arg("1")
            .arg("-f")
            .arg("f32le")
            .arg("pipe:1")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        // VEGAS Pro is a GUI process. Without this flag Windows creates a brand new
        // console window for every console child, so a project with many events would
        // flash hundreds of terminals. CREATE_NO_WINDOW keeps FFmpeg fully silent and
        // invisible while still allowing the stdin/stdout/stderr pipes above.
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            cmd.creation_flags(CREATE_NO_WINDOW);
        }

        let mut child = cmd.spawn().map_err(|e| {
            if e.kind() == std::io::ErrorKind::NotFound {
                FFMPEG_UNAVAILABLE.store(true, Ordering::Relaxed);
            }
            format!(
                "Failed to spawn FFmpeg at '{}': {}. Ensure FFmpeg is accessible.",
                bin, e
            )
        })?;

        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| "Failed to capture FFmpeg stdout".to_string())?;
        let stderr = child
            .stderr
            .take()
            .ok_or_else(|| "Failed to capture FFmpeg stderr".to_string())?;

        // Drain stderr on a dedicated thread. Leaving it unread can fill the OS pipe
        // buffer and deadlock FFmpeg (a classic cause of "the sync just stops").
        let stderr_handle = std::thread::spawn(move || {
            let mut reader = BufReader::new(stderr);
            let mut collected = Vec::with_capacity(4096);
            let mut buf = [0u8; 4096];
            loop {
                match reader.read(&mut buf) {
                    Ok(0) | Err(_) => break,
                    Ok(n) => {
                        if collected.len() < STDERR_LIMIT {
                            let room = STDERR_LIMIT - collected.len();
                            collected.extend_from_slice(&buf[..n.min(room)]);
                        }
                    }
                }
            }
            collected
        });

        let mut reader = BufReader::with_capacity(READ_CHUNK_BYTES, stdout);
        let mut raw = vec![0u8; READ_CHUNK_BYTES];
        let mut carry: Vec<u8> = Vec::with_capacity(4);
        let mut scratch: Vec<f32> = Vec::with_capacity(READ_CHUNK_BYTES / 4);
        let mut total: u64 = 0;

        let read_result: Result<(), String> = (|| {
            loop {
                let n = reader
                    .read(&mut raw)
                    .map_err(|e| format!("Failed to read decoded stream from FFmpeg: {}", e))?;
                if n == 0 {
                    break;
                }

                scratch.clear();
                if carry.is_empty() {
                    let usable = n - (n % 4);
                    scratch.reserve(usable / 4);
                    for chunk in raw[..usable].chunks_exact(4) {
                        scratch.push(f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]));
                    }
                    carry.extend_from_slice(&raw[usable..n]);
                } else {
                    let mut bytes = Vec::with_capacity(carry.len() + n);
                    bytes.extend_from_slice(&carry);
                    bytes.extend_from_slice(&raw[..n]);
                    let usable = bytes.len() - (bytes.len() % 4);
                    for chunk in bytes[..usable].chunks_exact(4) {
                        scratch.push(f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]));
                    }
                    carry = bytes[usable..].to_vec();
                }

                if !scratch.is_empty() {
                    total += scratch.len() as u64;
                    if !on_samples(&scratch) {
                        return Err(CANCELLED.to_string());
                    }
                }
            }
            Ok(())
        })();

        if read_result.is_err() {
            let _ = terminate(&mut child);
        }

        let status = child.wait();
        let stderr_text = stderr_handle.join().unwrap_or_default();
        let stderr_text = String::from_utf8_lossy(&stderr_text).trim().to_string();

        if let Err(e) = read_result {
            return Err(e);
        }

        match status {
            Ok(s) if s.success() => Ok(total),
            Ok(s) => {
                if total > 0 {
                    log_partial_decode(file_path, &s, total, duration_sec, target_rate);
                    Ok(total)
                } else if stderr_text.is_empty() {
                    Err(format!("FFmpeg exited with status {}", s))
                } else {
                    Err(format!("FFmpeg exited with status {}: {}", s, stderr_text))
                }
            }
            Err(e) => {
                if total > 0 {
                    log::warn!(
                        "FFmpeg wait failed for '{}' after decoding {} sample(s) ({:.2}s): {}; keeping the partial buffer.",
                        file_path,
                        total,
                        total as f64 / target_rate.max(1) as f64,
                        e
                    );
                    Ok(total)
                } else {
                    Err(format!("Failed to wait for FFmpeg: {}", e))
                }
            }
        }
    }
}

/// Logs a partial FFmpeg decode (non-zero exit but usable audio) and flags
/// output below 95% of the requested duration.
fn log_partial_decode(
    file_path: &str,
    status: &std::process::ExitStatus,
    total: u64,
    duration_sec: Option<f64>,
    target_rate: u32,
) {
    let decoded_seconds = total as f64 / target_rate.max(1) as f64;
    if let Some(duration) = duration_sec {
        if duration > 0.001 {
            let expected = (duration * target_rate as f64).round().max(1.0);
            let ratio = total as f64 / expected;
            let shortfall_seconds = ((expected - total as f64).max(0.0)) / target_rate.max(1) as f64;
            // Container duration can be approximate, so only call out a materially
            // short decode rather than every rounding difference.
            let flag = if ratio < 0.95 && shortfall_seconds > 0.5 {
                " (< 95% of expected; more than 0.5s missing)"
            } else {
                ""
            };
            log::warn!(
                "FFmpeg exited with status {} for '{}' after decoding {} sample(s), expected ~{} sample(s) ({:.1}s of {:.1}s){}; keeping the partial buffer.",
                status,
                file_path,
                total,
                expected as u64,
                decoded_seconds,
                duration,
                flag
            );
            return;
        }
    }

    log::warn!(
        "FFmpeg exited with status {} for '{}' after decoding {} sample(s) ({:.1}s); keeping the partial buffer.",
        status,
        file_path,
        total,
        decoded_seconds
    );
}

fn terminate(child: &mut Child) -> std::io::Result<()> {
    let _ = child.kill();
    let _ = child.wait();
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_video_container_detection() {
        assert!(AudioDecoder::is_video_container("C:/clips/cam_a.MP4"));
        assert!(AudioDecoder::is_video_container("C:/clips/cam_a.mov"));
        assert!(AudioDecoder::is_video_container("C:/clips/cam_a.mxf"));
        assert!(!AudioDecoder::is_video_container("C:/audio/recorder.wav"));
        assert!(!AudioDecoder::is_video_container("C:/audio/recorder.flac"));
    }
}
