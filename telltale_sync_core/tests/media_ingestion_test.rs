use std::fs::File;
use std::io::Write;
use vegas_sync_core::audio::AudioDecoder;

fn create_test_wav(file_path: &str, sample_rate: u32, duration_sec: f32) {
    let num_samples = (sample_rate as f32 * duration_sec) as usize;
    let num_channels: u16 = 2; // Stereo
    let bits_per_sample: u16 = 16;
    let byte_rate = sample_rate * num_channels as u32 * (bits_per_sample as u32 / 8);
    let block_align = num_channels * (bits_per_sample / 8);
    let data_len = num_samples * block_align as usize;
    let chunk_size = 36 + data_len as u32;

    let mut file = File::create(file_path).expect("Failed to create test WAV file");

    // RIFF Header
    file.write_all(b"RIFF").unwrap();
    file.write_all(&chunk_size.to_le_bytes()).unwrap();
    file.write_all(b"WAVE").unwrap();

    // fmt subchunk
    file.write_all(b"fmt ").unwrap();
    file.write_all(&16u32.to_le_bytes()).unwrap(); // Subchunk1Size (16 for PCM)
    file.write_all(&1u16.to_le_bytes()).unwrap(); // AudioFormat (1 = PCM)
    file.write_all(&num_channels.to_le_bytes()).unwrap();
    file.write_all(&sample_rate.to_le_bytes()).unwrap();
    file.write_all(&byte_rate.to_le_bytes()).unwrap();
    file.write_all(&block_align.to_le_bytes()).unwrap();
    file.write_all(&bits_per_sample.to_le_bytes()).unwrap();

    // data subchunk
    file.write_all(b"data").unwrap();
    file.write_all(&(data_len as u32).to_le_bytes()).unwrap();

    // Write samples: 440 Hz tone in left channel, 880 Hz in right
    for i in 0..num_samples {
        let t = i as f32 / sample_rate as f32;
        let left = ((2.0 * std::f32::consts::PI * 440.0 * t).sin() * 30000.0) as i16;
        let right = ((2.0 * std::f32::consts::PI * 880.0 * t).sin() * 30000.0) as i16;
        file.write_all(&left.to_le_bytes()).unwrap();
        file.write_all(&right.to_le_bytes()).unwrap();
    }
}

#[test]
fn test_real_wav_ingestion_and_conditioning() {
    let temp_dir = std::env::temp_dir();
    let test_wav_path = temp_dir.join("vegas_sync_test_stereo_48k.wav");
    let test_wav_str = test_wav_path.to_str().unwrap();

    create_test_wav(test_wav_str, 48000, 2.0); // 2 seconds of 48kHz stereo WAV

    let result = AudioDecoder::load_and_condition(test_wav_str, None, None, 16000, None);

    // Clean up
    let _ = std::fs::remove_file(&test_wav_path);

    assert!(result.is_ok(), "Failed to decode WAV: {:?}", result.err());
    let samples = result.unwrap();

    // At 16 kHz for 2 seconds, expected sample count is ~32000
    assert!(
        (samples.len() as isize - 32000).abs() < 500,
        "Sample count was {}, expected ~32000",
        samples.len()
    );

    // Assert normalized amplitude
    let max_amp = samples.iter().map(|&s| s.abs()).fold(0.0f32, |a, b| a.max(b));
    assert!(max_amp <= 1.0001);
    assert!(max_amp > 0.8);
}
