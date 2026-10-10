# 9. Using the Performance Screen

This chapter explains how to use the Performance screen to measure, adjust, and find limits.
For the meaning of each item, see [5. Performance Screen](./05_performance_screen.md).

## 9.1 Before You Start

### 9.1.1 Decide What to Measure

First decide what you want to check.

- Frequency response or distortion
  - Use the reference signals (tone / sweep / noise)
- Deck azimuth
  - Use a mid-to-high tone and the Lissajous display
- The highest settings Onta can use with your equipment
  - Use the Modulated tab (subcarriers and modulation) and the I-Q graph

### 9.1.2 Check the Wiring and Input/Output

1. Decide the measurement path:
   - WAV files only (digital path)
   - Audio input/output including real equipment (analog path)
2. If you use audio input/output, select the sending and receiving devices.
3. Start with low send and receive volumes so the level is not too high.

## 9.2 Basic Flow

Use the Performance screen in this order:

1. Choose the signal on the send side (Reference Signal or Modulated)
2. Set the duration and the output (WAV / audio)
3. Set the input (WAV / audio) on the receive side
4. Start both receiving and sending
5. Adjust the conditions while watching the displays
6. Record the results (settings, readings, findings)

## 9.3 Step 1: Measuring with Reference Signals

### 9.3.1 Checking Each Frequency with a Tone

1. In the Send panel, select the Reference Signal tab.
2. Choose a tone frequency (for example, 1kHz, 10kHz, or 15kHz).
3. Choose the output mode.
   - WAV Output: creates a WAV file to receive later. Choose the sampling rate (44100 Hz / 48000 Hz / 96000 Hz) to the right of Audio Output
   - Audio Output: sends to the device in real time. Playback uses the sampling rate of the selected device. The sampling rate setting is hidden
4. Set the input mode in the Receive panel to match (WAV or audio). Audio input is analyzed at the device's sampling rate, and WAV input at the file's sampling rate.
5. Press Start on the receive side, then Start on the send side.
6. Check the FFT, oscilloscope, frequency counter, and THD.

What to look for:

- The FFT peak is at the expected frequency
- The oscilloscope waveform has no clipping or large distortion
- The frequency reading is stable
- The THD does not jump up

### 9.3.2 Checking the Band with a Sweep

1. Select Sweep (20Hz-20KHz) on the send side.
2. Start receiving, then start sending.
3. On the FFT, look for the band where the level drops, and for peaks and dips.

Note:

- The wow display is not updated during a sweep (this is expected).

### 9.3.3 Checking the Overall Balance with White Noise

1. Select White Noise on the send side.
2. Start receiving and sending.
3. Look at the overall shape of the FFT for any imbalance across the band.

## 9.4 Step 2: Adjusting the Azimuth

1. Choose a mid-to-high tone on the send side (for example, 8kHz or 10kHz).
2. Switch the receive display to Lissajous / Wow / F Counter.
3. Adjust the azimuth of the playback deck little by little.
4. Find the position where all of these are true at once:
   - The Lissajous figure is close to the diagonal
   - The correlation is high
   - The wow value wobbles little
   - The frequency reading is stable
5. Once you find the position, keep it for tens of seconds to confirm the result is repeatable.

## 9.5 Step 3: Finding the Modulation Limit

### 9.5.1 Raising the Conditions Step by Step

1. Switch the send side to the Modulated tab.
2. Start with low settings.
   - For example, 16-SC + QPSK
3. Check the FFT and I-Q on the receive side.
   - Turn Modulated ON in the Receive panel and set the subcarriers and modulation to the same values as the send side before you start receiving
4. If everything looks good, raise the settings step by step.
   - With audio output, you can change the subcarriers and modulation without stopping. Change the Receive panel to the same values

- For example: 24-SC + QPSK, then 32-SC + 8PSK, then 40-SC + 16QAM

5. Note the settings at which the points start to spread or break down.

### 9.5.2 How to Judge the Limit

- Settings close to practical use
  - The I-Q points gather near each ideal position
  - No subcarriers are badly missing on the FFT
- Settings beyond the limit
  - The I-Q points spread widely
  - Groups rotate or get squashed
  - Only the high-frequency groups break down

Notes:

- 56-SC, 64-SC, and 72-SC are for decks with a wide frequency range. 256QAM is an extended setting for measurement only.
- Evaluate them separately from normal use of the File screen.

## 9.6 Recording Your Results

The Performance screen does not save history, so we recommend keeping notes of:

- Date and time
- Deck, tape, and connection path
- Send conditions (signal type, frequency, subcarriers, modulation, duration, volume)
- Receive conditions (input mode, device, volume)
- Results (FFT trend, wow, frequency, THD, I-Q findings)
- Whether to use the settings in practice

## 9.7 Troubleshooting

- The frequency reading is unstable
  - Lower or raise the input/output level
  - Check the cables and connections
  - Wait until the tape runs steadily
- The I-Q points scatter widely
  - Use a simpler modulation (64QAM, then 16QAM, 8PSK, QPSK)
  - Use fewer subcarriers (40-SC, then 32-SC, 24-SC)
  - Check the recording and playback volume
- Wow is not updated
  - Check that you are not using a sweep or noise
  - Measure again with a tone or a modulated signal
  - It also stops when the input is silent (below −60 dBFS), so check the input volume and connections
- Sending or receiving does not start
  - Check that an output mode (WAV / audio) is enabled
  - Check that the WAV file and devices are all selected

## 9.8 Quickest Path (When in Doubt)

1. Check the level and distortion with a 1kHz tone
2. Check the treble and the azimuth tendency with a 10kHz tone
3. Check how tightly the I-Q points gather with 24-SC + QPSK
4. If that works, raise the settings up to 32-SC + 16QAM to find the limit
5. Use the setting one step below the limit as your practical setting
