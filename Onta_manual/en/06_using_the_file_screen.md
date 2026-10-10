# 6. Using the File Screen

This chapter explains how to use the File screen, divided into sending and receiving.
For the meaning of each setting, see [2. File Screen](./02_file_screen.md).

## 6.1 Sending

### 6.1.1 Overview

Send in this order:

1. Decide the send conditions (Channel / Subcarrier / Modulation / Interleave)
   - Interleave x2 records the same content twice
2. Select the file to send
3. Choose the output (WAV Output or Audio Output)
4. Press Start, and check the result when it finishes

### 6.1.2 Steps (WAV Output)

1. In the Send panel, set:
   - Channel
   - Subcarrier
   - Modulation
   - Interleave
2. Select the file to send with Input File (送信ファイル). You can also drag and drop the file onto the path box. To remove the selection before choosing again, press Clear to its right (the WAV output destination stays).
3. Select WAV Output as the output mode.
4. To the right of Audio Output, choose the sampling rate (44100 Hz / 48000 Hz / 96000 Hz).
5. Specify the WAV output file. If the output is empty when you select the input file, a WAV name is filled in from the input file name.
6. Press Start.
7. When it finishes, check that the WAV file was created at the destination.

### 6.1.3 Steps (Audio Output)

1. Set the send conditions in the Send panel.
2. Select the file with Input File. You can also drag and drop the file onto the path box.
3. Select Audio Output as the output mode. The sampling rate setting is hidden.
4. Set the output device and volume. Playback uses the sampling rate of the device.
5. Connect to the cassette deck (or other recorder) and get it ready to record.
6. Press Start to begin sending.
7. Press Stop if you need to stop.

### 6.1.4 Recommended Settings

- If you are unsure about the tape quality or the deck
  - Use fewer subcarriers (for example, 16-SC or 24-SC)
  - Use a simpler modulation (for example, BPSK or QPSK)
- If you want the most reliable result
  - Set Interleave to x2

### 6.1.5 Notes

- You cannot start without an input file.
- You cannot start if neither WAV Output nor Audio Output is enabled.
- Large files take a long time.

## 6.2 Receiving

### 6.2.1 Overview

Receive in this order:

1. Choose the input mode (WAV Input / Audio Input)
2. Specify the source (a WAV file or an audio device)
3. Specify the output folder
4. Press Start, then check the restored file

### 6.2.2 Steps (WAV Input)

1. In the Receive panel, select WAV Input.
2. Select the WAV file to receive. Receiving uses the sampling rate of the file.
3. Specify the output folder.
4. Press Start.
5. While receiving, watch the progress and the Receive Details tab.
6. When it finishes, check that the restored file was created in the output folder. It is created only when all blocks are OK. The destination is shown in the completion message.

### 6.2.3 Steps (Audio Input)

1. Select Audio Input.
2. Set the input device and volume.
3. Specify the output folder.
4. Press Start.
5. Start playback on the cassette deck (or other player). It is fine if there is a pause between Start and playback; receiving begins when the start of the recording is detected.
6. When it is done, press Stop if needed. If all blocks are OK, the restored file is created in the output folder.

### 6.2.4 What to Watch While Receiving

- Progress: overall progress
- Wow/Flutter meter: how much the tape speed wobbles
- Error Rate: how much margin error correction has left
- I-Q: how tightly the received points gather (the more they spread, the less stable the reception)

### 6.2.5 Notes

- You cannot start without an output folder.
- With WAV input, you cannot start if no WAV file is selected or the file does not exist.
- Selecting a WAV file does not start receiving. Always press Start.
- Tape speed differences (up to about ±4%) are measured and corrected automatically. A WAV recorded from tape may have silence at the beginning.
- With audio input, if you press Start after the recording has already begun, the first file header is missed. Receiving can still start from a later file header (in the middle or at the end).
- If the sending PC's audio output drops out briefly (about 10 ms of silence), the receiver closes the gap and decodes normally.
- If some blocks are NG, receive the same tape (or WAV) again. You can start from a file header in the middle (one comes every 16 blocks) or just before an NG block (at its block header). Received blocks accumulate in the history, and once all blocks are complete, the history entry becomes COMPLETE and you can download it. When that receive ends (including when you press Stop), the restored file is also created in the output folder. When you start in the middle, blocks you did not receive are not shown as NG.
- With audio input, receiving does not stop even if NG blocks remain at the end. Rewind the tape and play it again to pick up the NG blocks (blocks that are already OK never go back to NG).
- If receiving starts at a block header and the file is in the receive history, the Receive Details tab shows the history results (OK / NG) and fills in the blocks as they are received. While a block is being received, the meter and the error rate / I-Q graphs move. For a file that is not in the history, only the received blocks are shown, in block number order (the file header, file size, and block count show "-", and the Total size is the sum of the OK blocks). Until the file header is received, these blocks are not in the receive history; they are saved as Unknown Blocks on the History screen (COMPLETE if the data was received, IN-COMPLETE if the data was damaged). They move to the receive history once the file header is received.
- An NG block shows 0% on its meter. When it starts being received again, its result goes back to "-" and the meter moves.
- With audio input, if the input goes almost silent while a block is being received (for example, the tape stopped or a cable came loose), that block becomes NG without waiting for the rest of its data, and Onta waits for the next block header. While it waits, the following blocks are not marked NG.

## 6.3 Troubleshooting

- The file cannot be restored
  - Lower the subcarriers and modulation on the send side and record again
  - Set Interleave to x2
  - Adjust the volume if it is too high or too low
- Receiving stops or does not progress
  - Check that the correct input device is selected
  - Check that you can write to the output folder
