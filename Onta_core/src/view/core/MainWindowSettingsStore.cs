using System.Text;
using Onta.Core;

namespace Onta.View.Core;

internal readonly record struct MainWindowSettings(
    SendSettingsSnapshot Send,
    ReceivePanel.ReceiveSettingsSnapshot Receive);

internal static class MainWindowSettingsStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ONTASET1");
    private const ushort Version = 1;

    public static bool TryLoad(string filePath, out MainWindowSettings settings)
    {
        settings = default;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
        {
            return false;
        }

        var version = reader.ReadUInt16();
        if (version != Version)
        {
            return false;
        }

        var sendChannel = (ChannelMode)reader.ReadByte();
        var sendSubcarriers = reader.ReadInt32();
        var sendModulation = (ModulationScheme)reader.ReadByte();
        var sendInterleave = reader.ReadInt32();
        var sendInputPath = reader.ReadString();
        var sendWriteWav = reader.ReadBoolean();
        var sendWavPath = reader.ReadString();
        var sendAudioDeviceNumber = reader.ReadInt32();
        var sendAudioVolume = reader.ReadDouble();

        var receiveUseWav = reader.ReadBoolean();
        var receiveWavPath = reader.ReadString();
        var receiveOutputDir = reader.ReadString();
        var receiveAudioDeviceNumber = reader.ReadInt32();
        var receiveAudioVolume = reader.ReadDouble();

        settings = new MainWindowSettings(
            Send: new SendSettingsSnapshot(
                ChannelMode: Enum.IsDefined(typeof(ChannelMode), sendChannel) ? sendChannel : ChannelMode.Mono,
                ActiveSubcarriers: sendSubcarriers,
                ModulationScheme: Enum.IsDefined(typeof(ModulationScheme), sendModulation) ? sendModulation : ModulationScheme.Bpsk,
                BlockInterleaveFactor: sendInterleave,
                InputFilePath: sendInputPath,
                WriteWav: sendWriteWav,
                WavOutputPath: sendWavPath,
                PlayAudio: !sendWriteWav,
                AudioDeviceNumber: sendAudioDeviceNumber,
                AudioDeviceName: string.Empty,
                AudioVolume: sendAudioVolume),
            Receive: new ReceivePanel.ReceiveSettingsSnapshot(
                UseWavInput: receiveUseWav,
                WavInputPath: receiveWavPath,
                OutputDirectory: receiveOutputDir,
                AudioDeviceNumber: receiveAudioDeviceNumber,
                AudioVolume: receiveAudioVolume));
        return true;
    }

    public static void Save(string filePath, MainWindowSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(Version);

        writer.Write((byte)settings.Send.ChannelMode);
        writer.Write(settings.Send.ActiveSubcarriers);
        writer.Write((byte)settings.Send.ModulationScheme);
        writer.Write(settings.Send.BlockInterleaveFactor);
        writer.Write(settings.Send.InputFilePath ?? string.Empty);
        writer.Write(settings.Send.WriteWav);
        writer.Write(settings.Send.WavOutputPath ?? string.Empty);
        writer.Write(settings.Send.AudioDeviceNumber);
        writer.Write(settings.Send.AudioVolume);

        writer.Write(settings.Receive.UseWavInput);
        writer.Write(settings.Receive.WavInputPath ?? string.Empty);
        writer.Write(settings.Receive.OutputDirectory ?? string.Empty);
        writer.Write(settings.Receive.AudioDeviceNumber);
        writer.Write(settings.Receive.AudioVolume);
    }
}
