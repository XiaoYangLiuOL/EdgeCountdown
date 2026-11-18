using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

[DataContract]
internal sealed class TimerPreferences
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeCountdown", "settings.json");
    private static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(TimerPreferences));

    [DataMember] internal string DurationText;
    [DataMember] internal bool? Topmost;
    [DataMember] internal SavedPlacement Placement;

    internal static TimerPreferences Load()
    {
        try
        {
            using (FileStream stream = File.OpenRead(SettingsFile))
                return (TimerPreferences)Serializer.ReadObject(stream) ?? new TimerPreferences();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SerializationException) { }
        return new TimerPreferences();
    }

    internal void Save()
    {
        string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
            using (FileStream stream = File.Create(temporary))
                Serializer.WriteObject(stream, this);

            if (File.Exists(SettingsFile))
                File.Replace(temporary, SettingsFile, null);
            else
                File.Move(temporary, SettingsFile);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SerializationException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

[DataContract]
internal sealed class SavedPlacement
{
    [DataMember] internal string Monitor;
    [DataMember] internal double LeftDip;
    [DataMember] internal double TopDip;
    [DataMember] internal bool OnRight;
    [DataMember] internal bool OnBottom;
}
