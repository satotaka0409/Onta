# 8. Using the Stream Screen

This chapter explains how to record music on a cassette tape with the Stream screen (stream recording) and play it back from the tape (stream playback).
For the meaning of each item, see [4. Stream Screen](./04_stream_screen.md).

## 8.1 What the Stream Feature Is

The stream feature records music, compressed with the Opus audio codec, continuously on a cassette tape as digital data.

- Difference from the File screen
  - File screen: sends one file in blocks; the receiver restores the original file
  - Stream screen: keeps sending sound without a break. **You can play back from the middle of a song**
- The title, artist, and cover art are recorded along with the sound and appear on screen during playback.
- Streams are **stereo only**.

## 8.2 Screen Layout

The top of the Stream screen is for sending (recording), and the bottom is for receiving (playback).

### 8.2.1 Send (Recording)

| Item | Description |
| :--- | :--- |
| Rate | Recording rate (audio bitrate). Choose from 18 / 20 / 23 / 24 / 28 / 30 / 33 / 44 / 50 Kbps |
| I/O | Source (File Input / Audio Input), output device, and volume |
| Input File | The audio file for file input (WAV / FLAC / MP3). Drag and drop onto the path box also works |
| Input Device | The recording device and volume for audio input |
| Output Device | The audio output device connected to the cassette deck, and its volume |
| Title / Artist | Track information to record (optional) |
| Cover Art | The image file to record (PNG / JPEG / BMP) and its format |
| Bytes / Size over limit | The size of the converted cover art. "Size over limit" appears above 4096 bytes |
| Start / Stop | Starts and stops recording (sending) |

- The send settings (rate, input/output, devices and volumes, title, artist, cover art) and the receive input device and volume are saved when you close Onta and restored the next time.
- If a previously used device cannot be found, the default device is used.

### 8.2.2 Receive (Playback)

| Item | Description |
| :--- | :--- |
| Rate | The rate detected from the stream being received |
| Input Device / Volume | The recording device connected to the cassette deck's output, and its volume |
| Output Device / Volume | The device that plays the received sound, and its volume (can be changed while receiving) |
| Title / Artist | The received track information |
| Cover Art | The received image, its size, and its bytes |
| Wow/Flutter | The small, quick speed wobble of the tape (L/R meters and time graph, ±0.5%) |
| Start / Stop | Starts and stops receiving |

### 8.2.3 Graphs

The bottom of the screen shows these graphs:

- Error Rate / FFT (switched with radio buttons)
  - Error Rate: how much error correction is needed while receiving (receive only)
  - FFT: the L (green) and R (orange) spectrum
- I-Q: the received points, colored by subcarrier group (A to I)

While receiving, the graphs show **the signal being received**. While sending, they show **the signal being sent**.

## 8.3 Choosing a Rate

A higher rate gives better sound but needs a tape in better condition, because it uses more complex modulation and higher frequencies.

| Label | Subcarriers / Modulation | Audio bitrate | Guide |
| :--- | :--- | :--- | :--- |
| 18Kbps | 48SC / 8PSK | 18.4 kbps | Most stable. For tapes in poor condition |
| 20Kbps | 56SC / 8PSK | 20.4 kbps | |
| 23Kbps | 64SC / 8PSK | 23.2 kbps | |
| 24Kbps | 48SC / 16QAM | 24.8 kbps | |
| 28Kbps | 56SC / 16QAM | 28.4 kbps | |
| 30Kbps | 64SC / 16QAM | 30.4 kbps | |
| 33Kbps | 72SC / 16QAM | 33.2 kbps | For decks whose response reaches about 16 kHz |
| 44Kbps | 64SC / 64QAM | 44.4 kbps | |
| 50Kbps | 72SC / 64QAM | 50.4 kbps | Best sound. For a tape and deck in very good condition |

- At every rate, recording takes about as long as the song itself.
- If you are unsure, first check that recording and playback work at 18Kbps, then raise the rate step by step.
- On decks or tapes that lose treble (above 10 kHz), 56SC, 64SC, and 72SC (20 / 23 / 28 / 30 / 33 / 44 / 50Kbps) tend to be unstable. 64QAM (44 / 50Kbps) is also sensitive to noise, so try it only after 30Kbps works reliably. Checking the FFT and I-Q on the Performance screen first is the surest way (see [9. Using the Performance Screen](./09_using_the_performance_screen.md)).

## 8.4 Step 1: Preparing to Record

### 8.4.1 Wiring

1. Connect the PC's audio output to the cassette deck's LINE input.
2. If you will also play back, connect the deck's LINE output to the PC's audio input.
3. Start with a modest recording level on the deck.

### 8.4.2 Choosing the Source

- File input
  1. Select File Input.
  2. Select an audio file (WAV / FLAC / MP3) with the "..." button. Drag and drop onto the path box also works. Other formats cannot be dropped.
- Audio input
  1. Select Audio Input.
  2. Set the input device (for example, line input or stereo mix) and its volume.
  3. With audio input, recording continues until you press Stop.

### 8.4.3 Choosing the Output Device

1. Select the device connected to the cassette deck as the output device. Playback uses the sampling rate of the device.
2. Adjust the output level with the volume slider.
   - Too loud, and the deck distorts the signal, which breaks up the I-Q points.
   - Too quiet, and the signal is buried in noise.

## 8.5 Step 2: Setting the Track Information and Cover Art

All track information is optional. **Items you leave empty are not recorded** (the items you do enter reach the receiver sooner).

### 8.5.1 Title / Artist

- Each can be up to 256 characters.
- When you select an input file (with the file button or by drag and drop), the file name without the extension is entered as the title. Edit it if needed.

### 8.5.2 Cover Art

1. Select an image file (PNG / JPEG / BMP) with Select File.
2. Choose the image format. The image is shrunk to that size and converted to PNG.

| Image format | Resolution | Color |
| :--- | :--- | :--- |
| 32x32 color | 32 × 32 | Color |
| 48x48 color | 48 × 48 | Color |
| 48x48 grayscale | 48 × 48 | Grayscale |
| 64x64 grayscale | 64 × 64 | Grayscale |

3. Check the converted size in Bytes.
   - The limit is 4096 bytes. Above that, "Size over limit" appears and you cannot start.
   - If it is over the limit, choose a smaller or grayscale format.
4. To send without cover art, press Clear to the right of Select File. The path box and preview are emptied, and no cover art is sent.

### 8.5.3 How Long the Cover Art Takes

Track information is sent a little at a time with each packet, rotating through title, artist, and cover art. Large cover art takes longer to arrive in full at the receiver.

| Cover art size | 18Kbps | 30Kbps | 50Kbps |
| :--- | :--- | :--- | :--- |
| 1024 bytes | about 34 s | about 21 s | about 13 s |
| 4096 bytes | about 2.2 min | about 1.4 min | about 51 s |

(Estimates when all three items, title, artist, and cover art, are sent)

- With a short song, the cover art may not arrive in full. **About 1024 bytes is recommended**.
- Track information is sent repeatedly, so even if you start playback in the middle of a song, it appears after a while.

## 8.6 Step 3: Recording

1. Set the rate, source, output device, and track information.
2. Put the cassette deck into recording.
3. Press Start in the send area.
   - While sending, you can change the rate, output device, and input/output volume.
     - The rate changes from the next packet. The receiver detects the rate automatically, so playback is not affected.
     - Switching the output device causes a short gap in the sound at that moment (also on the tape being recorded).
   - The source, input file, input device, and track information cannot be changed while sending.
   - You cannot start receiving while sending (sending and receiving cannot run at the same time).
4. Check the signal being sent on the FFT and I-Q graphs.
   - FFT: signal appears on both L and R from about 550 Hz up to the top of the subcarrier range
   - I-Q: points of the selected modulation (8PSK / 16QAM / 64QAM) are shown
5. To finish recording, press Stop in the send area. With file input, sending also ends automatically at the end of the file.
   - An end mark is recorded on the tape when file input ends automatically or when you press Stop, and with audio input when you press Stop. After Stop, the remaining sound and the end mark are sent before sending stops, so wait a few seconds before stopping the deck.
6. Stop the cassette deck.

## 8.7 Step 4: Playing Back

1. Connect the cassette deck's LINE output to the PC's audio input.
2. Set the receive input device and volume, and the output device and volume for playback.
3. Press Start in the receive area. Until a signal arrives, the status shows "Standby (low input level)" and the graphs do not move.
4. Play the tape. Starting in the middle of a song is fine. If the input level drops (for example, silence between songs), it returns to standby, and receiving resumes automatically when the signal returns.
5. Once receiving starts, you will see:
   - Rate: the rate used for recording, detected automatically
   - Title / Artist / Cover Art: each appears once all of its data has arrived
   - Cover art progress: how much has arrived (received pieces / total)
   - Status: "Receiving... Packets n (Errors m)", the number of received packets and of packets that could not be received
6. Check the reception on the graphs and adjust the input volume if needed.
   - FFT: signal appears on both L and R from about 550 Hz up to the top of the subcarrier range
   - I-Q: the points gather tightly around their ideal positions
   - Error Rate: lower means more margin. If it rises, check the volume and the deck
7. Press Stop in the receive area when you are done. When a tape recorded with an end mark reaches the end, the status shows "Ended".

Notes:

- When the stream changes (to a different recording), the displayed track information is cleared and collected again from the start.
- Parts of the track information with errors are discarded and collected again the next time the same part is sent.
- The input device records at its own sampling rate (such as 48000 Hz). Onta converts the sound internally, so playback works even if the sending PC and the devices use different sampling rates.

> **Limitation in this version**
> The received sound cannot be saved as a WAV file. Playback is through the output device (LINE output) only.

## 8.8 Troubleshooting

- Start cannot be pressed
  - Check that the cover art is not "Size over limit"
  - Check that the other side (send or receive) is not running
- "Failed: Input file was not found." appears right after Start
  - Check that an input file is selected for file input, and that it has not been moved or deleted
- The rate and track information do not appear
  - Check the input device and wiring
  - Raise or lower the input volume (the stream cannot be received if it is too low or too high)
  - You do not need to set the rate on the receive side; it is detected automatically
- The rate appears, but the track information takes a long time
  - Record at a lower rate (for example, 30Kbps, then 24Kbps, then 18Kbps)
  - Check the deck's recording level and the PC's output volume
  - Adjust the deck's azimuth (use the Lissajous display on the Performance screen)
- Many receive errors, or a large "Speed" value in the status
  - "Speed" is the estimated difference between playback speed and recording speed. Up to about ±4% is corrected automatically
  - If the value keeps swinging widely, or the wow/flutter meter and graph swing a lot, the tape is not running smoothly. Try cleaning the capstan and pinch roller, or playing on another deck
- The cover art takes a long time to appear
  - Record again with smaller cover art (about 1024 bytes is recommended)
  - Leaving the unused title or artist empty gives more room to the cover art

## 8.9 Quickest Path (When in Doubt)

1. Record a short song at 18Kbps with file input (enter only the title)
2. Play the tape and check that the rate and title appear
3. If the title appears quickly, raise the rate to 24Kbps, then 30Kbps, and record again
4. Record for real at the rate that was stable (keep the cover art around 1024 bytes)
