# Changelog

## v1.3.3
- **Installer & Licensing:** Switched to a standard x64 Windows MSI installer (`TellTale_WaveSync_Setup_v1.3.3.msi`) installing to `%ProgramData%\Vegas Pro\2027.0\Application Extensions` with automatic legacy cleanup, under the **Apache 2.0** license (**TellTale FX** / **Mohamed Anis**, [www.telltale.video](https://www.telltale.video)).
- **Native Engine (`telltale_sync_core.dll`):**
  - Renamed native audio engine binary to `telltale_sync_core.dll`.
  - Fixed short-clip (`< 15 s`) folder handling so valid acoustic matches align individually instead of being skipped.
  - Added multi-take & transitive relay matching across non-overlapping master chapters and secondary camera angles.
  - Improved long-take DSP consistency (streaming 300–4000 Hz bandpass + overlap-window zero-mean Pearson NCC + bounded `±0.75 s` fine GCC-PHAT).
  - Respected VEGAS `StreamIndex` for multi-stream audio containers and added hybrid fast + sample-accurate FFmpeg seeking.
  - Replaced fingerprint time-domain slicing with Hann-windowed FFT 16-band log-spectral flux and filtered discontinuity detection to RANSAC inliers.
- **VEGAS Pro Extension (`TellTaleWaveSync.dll`):**
  - All optional processing toggles now start **OFF** by default.
  - Replaced the placeholder speed-of-sound toggle with **Voice Activity Noise Gate** (`-40 dB`).
  - Improved `SelectedOnly` event scoping, grouped A/V deduplication, safe descending event splitting, and `1.0 ms` lane-reuse tolerance.
