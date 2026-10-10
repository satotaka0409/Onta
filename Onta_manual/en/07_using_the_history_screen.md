# 7. Using the History Screen

This chapter explains how to use the History screen to check results, get files, and tidy up the history.
For the meaning of each column, see [3. History Screen](./03_history_screen.md).

## 7.1 Basics

### 7.1.1 When You Open the Screen

1. Press Reload to load the latest history.
2. Check the counts in the status at the top.
   - Receive n / Send n / Unknown n
3. Choose the tab you want:
   - Receive History
   - Send History
   - Unknown Blocks

### 7.1.2 What Each Tab Is For

- Receive History
  - Check receive results, block counts, and the input type (Audio/WAV).
- Send History
  - Check past send conditions (subcarriers, modulation, mono/stereo).
- Unknown Blocks
  - Check blocks that could not yet be matched to a file.

## 7.2 Receive History

### 7.2.1 Checking a Receive Result

1. Open the Receive History tab.
2. Check the Result of the row:
   - COMPLETE: the file was fully restored
   - IN-COMPLETE: not complete
3. If needed, also check Block Count, Audio/WAV, and WAV File Name.

### 7.2.2 Checking Block Details

1. Click Received At on the row.
2. The block details (BLK, Result, Subcarriers, Modulation, Stereo/mono, Block Size) open below the row.
3. Click Received At again to close them.

### 7.2.3 Downloading a Restored File

1. Find a receive row that shows a Download button.
2. Press Download.
3. Choose the location and file name in the save dialog.
4. Check that the result is shown in the status at the top.

Notes:

- Some rows do not show a Download button.
- This is normal when the history has no data that can be written out for that row.

## 7.3 Send History

### 7.3.1 Rechecking Send Settings

1. Open the Send History tab.
2. Check Subcarriers, Modulation, and Stereo/mono of the row.
3. If needed, also check Audio/WAV and WAV File Name.

Examples:

- When a receive fails, compare the settings with a send that succeeded
- Compare sends of the same file with different settings

## 7.4 Unknown Blocks

### 7.4.1 Checking What Happened

1. Open the Unknown Blocks tab.
2. Check Result, Subcarriers, Modulation, and Stereo/mono.
3. Also look at Block Position, File Hash, and Block Hash to see whether the blocks come from the same file or position.

### 7.4.2 Narrowing Down the Cause

1. Find the conditions that produce many unknown blocks.
2. Check the settings used around the same time in the Send History tab.
3. Next time, send with conditions one step lower and compare.
   - For example, use a simpler modulation or fewer subcarriers

## 7.5 Deleting History

Delete history acts on the selected row of the current tab (on the Unknown Blocks tab, it deletes only the selected unknown block).

1. Select the row to delete.
2. Press Delete history.
3. Check the confirmation dialog and choose Yes.
4. After the automatic reload, check that the row is gone.

Caution:

- Deletion cannot be undone.
- Download anything you need before deleting.

## 7.6 Common Tasks

### 7.6.1 Finding the Cause of a Failed Receive

1. Find IN-COMPLETE rows in the receive history
2. Open the block details of the row and look for a pattern in the failures
3. Check the Unknown Blocks tab for blocks received around the same time
4. Check the send conditions in the send history and decide the conditions for the next try

### 7.6.2 Making a Template of Settings That Work

1. Look at several COMPLETE rows
2. Find the subcarriers, modulation, and mono/stereo they have in common
3. Use them as your starting settings for the next send

## 7.7 Troubleshooting

- The history is not up to date
  - Press Reload
- Delete history cannot be pressed
  - Select a row in one of the tabs, then try again
- Download is not shown
  - The row has no data that can be written out. Check another COMPLETE row
- The counts do not seem to add up
  - The receive history and unknown blocks are counted separately, so check both tabs. Unknown blocks are not shown in the receive history. When the file header is received, they move to the receive history and are no longer counted as unknown blocks
