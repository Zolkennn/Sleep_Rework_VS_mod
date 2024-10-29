using CommonLib.Config;

namespace Custom_Sleep;

[Config("SleepRework.json")]
public class Config
{
    [Description("Enable the seeping percent")]
    public bool SleepingPercent { get; set; } = true;
    
    [Description("Can the players nap the day ? (perfect for RP)")]
    public bool Nap { get; set; } = true;
    
    [Description("Does the game speed during the sleep instantly stop")]
    public bool instantStop { get; set; } = true;
    
    [Description("Can the player only sleep during valid time")]
    public bool ValidHours { get; set; } = true;

    [Description("At what hours the player automaticly wakeup (need ValidHours)")]
    [Range(0, 23)]
    public ushort MorningHours { get; set; } = 8;
    
    [Description("At what hours the player can start to sleep (need ValidHours)")]
    [Range(0, 23)]
    public ushort  EveningHours { get; set; } = 22;
    
    
}