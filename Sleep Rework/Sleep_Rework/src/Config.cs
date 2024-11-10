using CommonLib.Config;

namespace Custom_Sleep;

[Config("SleepRework.json")]
public class Config
{
    [Description("Percent of player spleeping to skip the night")]
    [Range(0.0f, 1.0f)]
    public float SleepingPercent { get; set; } = 0.8f;
    
    [Description("Count creative player in the percent")]
    public bool CountCreativePlayers { get; set; } = true;
    
    [Description("Count spectator player in the percent")]
    public bool CountSpectatorPlayers { get; set; } = false;
    
    [Description("Can the players nap the day ? (perfect for RP)")]
    public bool Nap { get; set; } = true;
    
    [Description("Does the game speed during the sleep instantly stop Not Implemented Yet")]
    public bool InstantStop { get; set; } = true;
    
    [Description("Can the player only sleep during valid time Not Implemented Yet")]
    public bool ValidHours { get; set; } = true;

    [Description("At what hours the player automaticly wakeup (need ValidHours)")]
    [Range(0, 23)]
    public ushort MorningHours { get; set; } = 8;
    
    [Description("At what hours the player can start to sleep (need ValidHours)")]
    [Range(0, 23)]
    public ushort  EveningHours { get; set; } = 22;
    
    
}