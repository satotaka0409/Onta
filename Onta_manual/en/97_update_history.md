# Update History

Changes to Onta, newest first.

## 1.0.0 (2026-09-27) First Release

The first public release. With four screens (File, History, Stream, and Performance), Onta records data on cassette tape as sound and recovers it by playing the tape back.

### File Transfer

- Converts a file to sound and sends it, and restores the original file from a WAV file or audio input
- Choose 16 to 72 subcarriers, BPSK / QPSK / 8PSK / 16QAM / 64QAM modulation, and mono or stereo
- Interleave (x1 / x2) records the same data twice at different times. The second copy automatically uses more conservative settings
- WAV output can be 44100 Hz, 48000 Hz, or 96000 Hz. Audio output is converted to the sampling rate of the selected device
- Files are handled in blocks of up to 16,384 bytes. Even if you start playback in the middle, missing blocks can be filled in later
- When all blocks are complete, the file is saved to the output folder under its name. If a different file with the same name exists, a number is added
- Wow/flutter, error rate, FFT, and I-Q (colored by groups A to I) are shown while receiving
- Send Details and Receive Details show the time, size, and progress of each part

### History

- Keeps the receive history, send history, and unknown blocks
- Fully received files can be downloaded from the History screen
- Blocks received without the file header are kept as unknown blocks, and move into the receive history when the file header of the same file is received later

### Stream

- Compresses music with Opus and records it continuously on tape in stereo. Playback can start in the middle of a song
- Nine rates from 18Kbps to 50Kbps. The receiver detects the recording rate automatically
- Input can be WAV / FLAC / MP3 or audio input. The title, artist, and cover art are recorded repeatedly and shown during playback
- Estimates and corrects tape speed differences, and shows wow/flutter

### Performance

- Checks the usable range of decks and cables with tones (315Hz to 20kHz), sweeps, white noise, and the same modulated signal Onta uses for data
- Modulation can be BPSK to 64QAM, plus 256QAM on this screen only
- Shows FFT, oscilloscope, Lissajous, frequency counter, distortion rate, wow/flutter, and I-Q. Nothing is saved to files or history
