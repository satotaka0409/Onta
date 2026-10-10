# 1. What Is Onta?

## 1.1 Overview

Onta (音多) is an application that sends and receives files as audio signals.<br>
The sender turns a file into sound, and the receiver restores the original file from that sound.<br>
The intended use is to record PC files on a cassette tape as digital data,<br>
then play the tape back into the PC to get the original files again.<br>
In short, it is a project to use a cassette tape in place of a USB memory stick.<br>
<br>
About fifty years ago, in the dawn of personal computing, computers were very modest.<br>
Main memory was 16 KB (not 16 MB), with an 8-bit CPU running at about 4 MHz.<br>
People connected them to a home TV and played games on a blurry screen.<br>
Programs and data were saved on cassette tapes, recorded as sound.<br>
Standards such as the Kansas City Standard appeared, and publishers sold games on cassette.<br>
The transfer rate was about 600 baud, roughly 60 bytes per second.<br>
Recording 1 MB of data took about 4.6 hours,<br>
and a small scratch or worn spot on the tape could make it unreadable.<br>
This project revisits that idea with modern technology, aiming for more speed and more reliability.<br>
<br>
Recording whole files is the core of the application.<br>
In addition, there are two more ways to use it: Stream and Performance.<br>
Stream records music continuously on tape as a flow of digital data.<br>
Unlike a file, it does not have to be read from the beginning; you can start playback in the middle of a song.<br>
The title, artist, and cover art are recorded repeatedly along with the music and appear on screen during playback.<br>
Performance checks how far your cassette deck, tape, and cables can go.<br>
It plays tones, sweeps, and the same kind of signal Onta uses for data, and shows frequency response, distortion, wow/flutter, and modulation limits on graphs.<br>
Nothing is saved to files or history on this screen. Use it to check the signal path before recording.<br>
<br>
The screens are designed to be fun to watch.<br>
FFT, error rate, and I-Q graphs move in real time.<br>
The Performance screen adds an oscilloscope, a Lissajous figure, and a wow/flutter meter.<br>
If the File screen makes you smile, you may be an expert in this field.<br>
<br>
Onta uses modern communication technologies such as OFDM (orthogonal frequency-division multiplexing), QAM (quadrature amplitude modulation), and error-correcting codes.<br>
The same technologies are used every day in digital TV, smartphones, and Wi-Fi, so Onta is also a good way to get a feel for them.<br>

## 1.2 Main Uses

- Offline sending and receiving with WAV files
- Real-time sending and receiving through audio devices
- Checking send/receive history and tracking results
- Stream recording and playback of music (can be received from the middle of a song)
- Measuring the performance of decks, tapes, and cables

## 1.3 What You Can Do with Onta

- Send a file as an audio signal
- Receive from a WAV file or an audio input and restore the original file
- Review the send and receive history
- Watch receive progress and per-block results
- Record music continuously as a stream and play it back with title, artist, and cover art
- Measure frequency response and modulation limits with tones, sweeps, and modulated signals

## 1.4 System Requirements

- OS: Windows 10 64-bit or later
- CPU: x64 or ARM64
- Recommended
  - CPU: Intel 8th-generation Core i series or equivalent, or better
  - RAM: 16 GB or more
- An audio input/output device available in Windows is required for audio input/output
- Notes
  - Sending and receiving with WAV files works even without an audio device
  - Recording to a device that uses lossy compression (MP3, AAC, etc.) does not work well
  - Do not connect to the PC's microphone jack; the PC treats the signal as noise. Use a LINE input or similar
  - If you do not have a cassette deck, you can connect two PCs with an audio cable and use one as the sender and the other as the receiver

## 1.5 Display Language

The screens are available in Japanese and English.

- At the start of installation (`Install-Onta.cmd` in the `Onta` folder extracted from the distributed installer `Onta-install-vX.Y.Z.exe`), you choose the display language (日本語 / English). The initial choice is Japanese if Windows is set to Japanese, English otherwise. Onta then starts in that language
- If no language was chosen (for example, when `setup.exe` was run directly), Onta follows the Windows display language: Japanese if Windows is set to Japanese, English otherwise
- You can also choose the language with the `--lang` startup option. It takes priority over the language chosen at installation
  - `Onta.exe --lang ja`: start in Japanese
  - `Onta.exe --lang en`: start in English
  - Any other language, or an invalid value, starts in English
- The language is decided at startup. To switch, close Onta and start it again

## 1.6 Basic Terms

- Send: converting a file into an audio signal and outputting it
- Receive: analyzing an audio signal and restoring the file
- WAV input/output: using a WAV file instead of an audio device
- Block: the unit a file is split into for sending and receiving (up to 16,384 bytes; only the last block may be shorter)
- Stream: recording music as a continuous flow of data that can be played back from any point
- Performance: a mode for checking the frequency response, distortion, wow/flutter, and modulation limits of a deck and tape

## 1.7 The Following Chapters

- Chapter 2 explains the File screen and its settings.
- Chapter 3 explains the History screen and how to use it.
- Chapter 4 explains the Stream screen.
- Chapter 5 explains the Performance screen.
